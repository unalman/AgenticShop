using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;

namespace AgenticShop.Ordering.Endpoints;

/// <summary>
/// Places an order: resolve every line against Catalog, hold it in Stock, settle the holds, and
/// write the order exactly once.
/// </summary>
/// <remarks>
/// <para>
/// This is the one collaborator the project's "no service layer" rule was written to allow.
/// <c>docs/DECISIONS.md</c> defers that rule's revisit to "a use case that needs to coordinate an
/// external call inside one transaction", and names <c>POST /orders</c> as the case. It has no
/// interface, no second consumer and one public method; the endpoint still owns binding, the result
/// and the status code. It lives beside the endpoint that uses it rather than in <c>Domain/</c>,
/// because it needs the <see cref="OrderingDbContext"/> and the clients, and the domain must not
/// reference either.
/// </para>
/// <para>
/// <b>The order row is written once, at the end, already terminal.</b> Nothing is persisted before
/// the downstream calls, so no reader can ever observe a <see cref="OrderStatus.Pending"/> order and
/// there is no "insert then update" that can fail between the two. The cost is the accepted
/// dual-write residual: if this process dies mid-confirm, Stock holds confirmed reservations for an
/// order id that was never written. See <c>docs/ROADMAP.md</c> → "Confirm-phase failure".
/// </para>
/// <para>
/// <b>Cancellation stops at the first side effect.</b> Resolving against Catalog honours the
/// caller's token, because nothing has happened yet and abandoning is free. From the first reserve
/// onward every call uses <see cref="CancellationToken.None"/>: a client that hangs up must not
/// leave stock held against an order nobody recorded. Those calls are still bounded — by
/// <c>HttpClient.Timeout</c> and by EF's command timeout — so this cannot hang the request.
/// </para>
/// </remarks>
public sealed class OrderPlacer(
    OrderingDbContext db,
    ICatalogClient catalog,
    IStockClient stock,
    ILogger<OrderPlacer> logger)
{
    /// <summary>Used from the first side-effecting call onward; see the remarks.</summary>
    private static readonly CancellationToken NotCancellable = CancellationToken.None;

    public async Task<Order> PlaceAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var wanted = CombineLines(request.Lines);
        var resolved = await ResolveAsync(wanted, cancellationToken);

        // The id is minted before the aggregate exists because Stock's reservations carry it, and
        // because OrderNumber is derived from it — which is what removes the need for a sequence.
        var orderId = Guid.NewGuid();

        var lines = resolved
            .Select(line => OrderLine.Create(
                orderId,
                line.Product.Id,
                line.Product.Name,
                line.Product.Price,
                line.Quantity))
            .ToList();

        var order = Order.Create(orderId, resolved[0].Product.Currency, lines);

        var reservations = await ReserveAsync(order, resolved);

        return await ConfirmAsync(order, reservations);
    }

    /// <summary>
    /// Sums duplicate product lines into one. Stock's <c>UNIQUE(order_id, stock_item_id)</c> allows
    /// one hold per product per order, so a second line for the same product would simply be
    /// refused; combining is lossless because the unit price comes from Catalog and is identical for
    /// both. First-appearance order is preserved, so which line fails first is deterministic.
    /// </summary>
    private static IReadOnlyList<WantedLine> CombineLines(IReadOnlyList<CreateOrderLineRequest> lines)
    {
        var combined = new List<WantedLine>(lines.Count);
        var indexByProduct = new Dictionary<Guid, int>(lines.Count);

        foreach (var line in lines)
        {
            if (indexByProduct.TryGetValue(line.ProductId, out var index))
            {
                var existing = combined[index];

                // Each quantity passed validation, but their sum can still exceed the bound.
                var total = existing.Quantity + line.Quantity;
                ArgumentOutOfRangeException.ThrowIfGreaterThan(total, OrderLine.MaxQuantity, nameof(lines));

                combined[index] = existing with { Quantity = total };
                continue;
            }

            indexByProduct[line.ProductId] = combined.Count;
            combined.Add(new WantedLine(line.ProductId, line.Quantity));
        }

        return combined;
    }

    /// <summary>
    /// Resolves and snapshots every line before anything is mutated anywhere, so an unorderable
    /// product or a mixed-currency order costs no compensation.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedLine>> ResolveAsync(
        IReadOnlyList<WantedLine> wanted,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedLine>(wanted.Count);
        var currencies = new List<string>(1);

        foreach (var line in wanted)
        {
            var product = await catalog.GetProductAsync(line.ProductId, cancellationToken)
                ?? throw new ProductUnavailableException(line.ProductId);

            // Defence in depth. Catalog's query filter already hides inactive products, so this is
            // reachable only if that filter is removed or an includeInactive path is added.
            if (!product.IsActive)
            {
                throw new ProductUnavailableException(line.ProductId);
            }

            if (!currencies.Contains(product.Currency, StringComparer.Ordinal))
            {
                currencies.Add(product.Currency);
            }

            // Reported as soon as a second currency appears rather than after resolving everything:
            // the caller cannot act on a third currency until the first two are fixed, and
            // resolving the rest would mean more calls for a request that is already rejected.
            if (currencies.Count > 1)
            {
                throw new MixedCurrencyException(currencies);
            }

            resolved.Add(new ResolvedLine(product, line.Quantity));
        }

        return resolved;
    }

    /// <summary>
    /// Holds every line, and returns the reservation ids in line order.
    /// </summary>
    /// <remarks>
    /// The ids live only for the duration of this placement and are deliberately never persisted:
    /// Stock owns them, and a copy here would be a second source of truth that could drift. Later
    /// reconciliation goes through <see cref="IStockClient.ListByOrderAsync"/>, which is keyed by
    /// order id and reports the product id every line can be matched on.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> ReserveAsync(Order order, IReadOnlyList<ResolvedLine> resolved)
    {
        var reservations = new List<Guid>(resolved.Count);

        foreach (var line in resolved)
        {
            Guid? reservationId;

            try
            {
                reservationId = await stock.ReserveAsync(line.Product.Id, order.Id, line.Quantity, NotCancellable);
            }
            catch (DownstreamServiceException exception)
            {
                logger.LogError(exception,
                    "Stock could not be reached while reserving product {ProductId} for order {OrderId}.",
                    line.Product.Id, order.Id);

                // A timed-out reserve may have created a hold whose 201 was never seen, so read the
                // authoritative state back and release whatever is pending. The outcome is
                // deliberately ignored: no confirm has been issued and the order id is fresh, so
                // nothing can be Confirmed and Failed stays truthful even if the read failed too.
                await ReconcileAsync(order);

                await WriteAsync(order, OrderStatus.Failed);

                throw new OrderPlacementIncompleteException(order.Id, order.Status);
            }

            if (reservationId is null)
            {
                // A clean refusal. The holds taken so far are known and still Pending, so they can
                // be released without a read — which matters because under contention this is the
                // hot path, and a reconciliation read per rejected order would be a real cost.
                await ReleaseAsync(reservations);

                await WriteAsync(order, OrderStatus.Failed);

                throw new StockUnavailableException(line.Product.Id, order.Id);
            }

            reservations.Add(reservationId.Value);
        }

        return reservations;
    }

    /// <summary>
    /// Settles every hold, then applies the confirm-phase policy: never release a confirmed
    /// reservation, reconcile an ambiguous failure by reading the authoritative state back, and
    /// record what actually happened rather than what was hoped for.
    /// </summary>
    private async Task<Order> ConfirmAsync(Order order, IReadOnlyList<Guid> reservations)
    {
        var settled = 0;

        foreach (var reservationId in reservations)
        {
            try
            {
                if (await stock.ConfirmAsync(reservationId, NotCancellable))
                {
                    settled++;
                    continue;
                }
            }
            catch (DownstreamServiceException exception)
            {
                logger.LogError(exception,
                    "Stock could not be reached while confirming reservation {ReservationId} for order " +
                    "{OrderId}; {Settled} of {Total} lines were already confirmed.",
                    reservationId, order.Id, settled, reservations.Count);
            }

            // A false here is genuinely ambiguous: Stock answers 409 both for "already confirmed",
            // which decision D3 says must be treated as success, and for an xmin conflict, which
            // must not. Only the read below can tell them apart, so it is never guessed at.
            break;
        }

        if (settled == reservations.Count)
        {
            return await WriteAsync(order, OrderStatus.Confirmed);
        }

        var outcome = await ReconcileAsync(order);

        // AllConfirmed covers the case the loop could not see: the confirm applied and only its
        // response was lost. That is a success, and reporting it as anything else would have the
        // caller retry an order that has already shipped.
        if (outcome == ReconciliationOutcome.AllConfirmed)
        {
            return await WriteAsync(order, OrderStatus.Confirmed);
        }

        // Unknown is recorded as PartiallyConfirmed rather than Failed: "we do not know whether
        // something shipped" must never be written down as "nothing shipped".
        var status = outcome == ReconciliationOutcome.NoneConfirmed
            ? OrderStatus.Failed
            : OrderStatus.PartiallyConfirmed;

        await WriteAsync(order, status);

        throw new OrderPlacementIncompleteException(order.Id, order.Status);
    }

    /// <summary>
    /// Reads back what Stock actually holds for this order, releasing whatever is still Pending.
    /// </summary>
    /// <remarks>
    /// Releases are issued for <c>Pending</c> rows only. A <c>Confirmed</c> one is never released:
    /// Stock refuses it, and even if it did not, release would return the hold without returning the
    /// stock, leaving counters that describe inventory which has already left.
    /// </remarks>
    private async Task<ReconciliationOutcome> ReconcileAsync(Order order)
    {
        IReadOnlyList<StockReservationSnapshot> snapshots;

        try
        {
            snapshots = await stock.ListByOrderAsync(order.Id, NotCancellable);
        }
        catch (DownstreamServiceException exception)
        {
            logger.LogError(exception,
                "Could not read back the reservations for order {OrderId}; its outcome is unknown and " +
                "it will be recorded as needing reconciliation.",
                order.Id);

            return ReconciliationOutcome.Unknown;
        }

        var confirmed = snapshots
            .Where(snapshot => snapshot.IsConfirmed)
            .Select(snapshot => snapshot.ProductId)
            .ToHashSet();

        await ReleaseAsync(snapshots
            .Where(snapshot => snapshot.IsPending)
            .Select(snapshot => snapshot.Id)
            .ToList());

        if (confirmed.Count == 0)
        {
            return ReconciliationOutcome.NoneConfirmed;
        }

        // Matched on product id rather than by count: Stock reports one reservation per product per
        // order, and product id is what an order line carries, so the two sides can be compared
        // without Ordering having kept anything Stock owns.
        return order.Lines.All(line => confirmed.Contains(line.ProductId))
            ? ReconciliationOutcome.AllConfirmed
            : ReconciliationOutcome.SomeConfirmed;
    }

    /// <summary>
    /// Best effort by design. A release that fails — refused, or Stock unreachable — is logged and
    /// does not change how the order is classified, because the classification describes what
    /// shipped and a stranded hold ships nothing. The hold stays until Phase 3's expiry worker.
    /// </summary>
    private async Task ReleaseAsync(IReadOnlyList<Guid> reservationIds)
    {
        foreach (var reservationId in reservationIds)
        {
            try
            {
                if (await stock.ReleaseAsync(reservationId, NotCancellable))
                {
                    continue;
                }

                // Refused. Benign when it means "already released", a stranded hold when it means an
                // xmin conflict; the status code cannot distinguish them and the outcome is the same
                // either way, so this is a warning rather than an error.
                logger.LogWarning(
                    "Stock refused to release reservation {ReservationId}; if it is still pending the " +
                    "hold is stranded until reservation expiry exists.",
                    reservationId);
            }
            catch (DownstreamServiceException exception)
            {
                logger.LogError(exception,
                    "Could not release reservation {ReservationId}; the hold is stranded until " +
                    "reservation expiry exists.",
                    reservationId);
            }
        }
    }

    /// <summary>
    /// The one and only write of the placement path: transition, add, commit. Every caller reaches
    /// it exactly once, which is what makes the row write-once.
    /// </summary>
    private async Task<Order> WriteAsync(Order order, OrderStatus status)
    {
        switch (status)
        {
            case OrderStatus.Confirmed:
                order.Confirm();
                break;
            case OrderStatus.Failed:
                order.Fail();
                break;
            case OrderStatus.PartiallyConfirmed:
                order.MarkPartiallyConfirmed();
                break;
            default:
                throw new InvalidOperationException(
                    $"Order {order.Id} cannot be persisted as {status}: Phase 0 writes an order once, already terminal.");
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(NotCancellable);

        return order;
    }

    private sealed record WantedLine(Guid ProductId, int Quantity);

    private sealed record ResolvedLine(CatalogProduct Product, int Quantity);

    private enum ReconciliationOutcome
    {
        /// <summary>Every line of the order is Confirmed in Stock.</summary>
        AllConfirmed,

        /// <summary>At least one line is Confirmed and at least one is not.</summary>
        SomeConfirmed,

        /// <summary>Nothing is Confirmed, so nothing shipped.</summary>
        NoneConfirmed,

        /// <summary>Stock could not be read back, so the outcome is unknown.</summary>
        Unknown
    }
}
