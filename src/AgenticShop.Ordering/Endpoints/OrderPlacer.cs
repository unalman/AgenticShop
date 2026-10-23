using System.Diagnostics;
using System.Text.Json;
using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AgenticShop.Ordering.Endpoints;

/// <summary>
/// Places an order: resolve every line against Catalog, write the order as <c>Pending</c>, and queue
/// the event that gets it fulfilled.
/// </summary>
/// <remarks>
/// <para>
/// This is the one collaborator the project's "no service layer" rule was written to allow.
/// <c>docs/DECISIONS.md</c> defers that rule's revisit to "a use case that needs to coordinate an
/// external call inside one transaction", and names <c>POST /orders</c> as the case. It has no
/// interface, no second consumer and one public method; the endpoint still owns binding, the result
/// and the status code. It lives beside the endpoint that uses it rather than in <c>Domain/</c>,
/// because it needs the <see cref="OrderingDbContext"/> and the Catalog client, and the domain must
/// not reference either.
/// </para>
/// <para>
/// <b>The order row is written first, as <see cref="OrderStatus.Pending"/>, and settled later by
/// something else.</b> That reverses Phase 0's rule, and the reversal is the point of Phase 2. The old
/// rule wrote the row once at the end, already terminal, so no reader could ever observe a
/// <c>Pending</c> order — but it could only do that by making the downstream calls first, which left
/// the accepted dual-write residual: a process dying mid-confirm left Stock holding committed
/// reservations for an order id that was never written. Writing the row first, in the same transaction
/// as the event that asks for the work, means a crash afterwards leaves an order that can still be
/// fulfilled. The residual is closed rather than documented.
/// </para>
/// <para>
/// <b>There is no compensation left in this class.</b> Reserve, confirm, reconcile and release all went
/// with the synchronous path: Stock holds every line of an order or none of them, so there is no partial
/// state here to undo, and what used to be O14's confirm-phase policy is now a consumer on the other
/// side of a broker. The only failure this path can still produce is Catalog's, and that happens before
/// anything is written.
/// </para>
/// <para>
/// <b>Cancellation stops at the first side effect.</b> Resolving against Catalog honours the caller's
/// token, because nothing has happened yet and abandoning is free. From the claim onward every call
/// uses <see cref="CancellationToken.None"/>: a client that hangs up must not leave a key claimed with
/// no order behind it, which would strand that key for good. Those calls are still bounded — by EF's
/// command timeout — so this cannot hang the request.
/// </para>
/// </remarks>
public sealed class OrderPlacer(
    OrderingDbContext db,
    ICatalogClient catalog)
{
    /// <summary>Used from the first side-effecting call onward; see the remarks.</summary>
    private static readonly CancellationToken NotCancellable = CancellationToken.None;

    public async Task<Order> PlaceAsync(
        CreateOrderRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
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

        // Claimed after Resolve and before the write, and that placement is still the whole design.
        // Resolve is pure reads, so a bad basket — an unorderable product, a mixed currency — fails
        // before the key is consumed and the client can retry the same key once it has fixed the
        // request. The write is now the first side effect, so the claim has to be committed before it:
        // a duplicate arriving mid-placement must find the row and be refused rather than placing
        // alongside us.
        var claim = await ClaimAsync(idempotencyKey);

        return await WriteAsync(order, claim);
    }

    /// <summary>
    /// Takes the key's slot, or refuses the request if something else already holds it.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="NotCancellable"/> like every other write here. The claim is the first side
    /// effect of the placement, so honouring a client cancellation from this point on could commit
    /// the row and then abandon it, stranding the key for good — the one outcome worse than ignoring
    /// the cancellation. A stranded claim still fails closed: it blocks one key and can never cause a
    /// second placement.
    /// </remarks>
    private async Task<OrderIdempotencyKey> ClaimAsync(string idempotencyKey)
    {
        var claim = OrderIdempotencyKey.Create(idempotencyKey, DateTimeOffset.UtcNow);

        db.IdempotencyKeys.Add(claim);

        try
        {
            await db.SaveChangesAsync(NotCancellable);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Someone else won the race. Detach rather than leave a doomed entity tracked, so the
            // caller's context stays usable for the replay path.
            db.Entry(claim).State = EntityState.Detached;

            throw new IdempotencyKeyInUseException(idempotencyKey);
        }

        return claim;
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
    /// The one and only write of the placement path: add the order, complete the key, queue the event,
    /// commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four things in one <c>SaveChangesAsync</c> — the order, its lines, the idempotency key's
    /// completion and the <c>OrderPlaced</c> outbox row. EF wraps them in one transaction, so there is
    /// no window in which an order exists without its key pointing at it (the window a retry would
    /// exploit) and none in which the order exists without the event that gets it fulfilled.
    /// </para>
    /// <para>
    /// <b>The order is written <see cref="OrderStatus.Pending"/>, which reverses Phase 0's rule.</b>
    /// That rule existed because nothing could advance a <c>Pending</c> row — there was no worker. There
    /// is one now, and writing <c>Pending</c> is the point: the row exists before the work does, so a
    /// crash after this commit leaves an order that can still be fulfilled rather than stock committed
    /// against an order nobody recorded. That was the dual-write residual, and the outbox is what
    /// closes it.
    /// </para>
    /// <para>
    /// The status code recorded on the key is a constant now, because a successful placement has exactly
    /// one outcome. The failures that used to be recorded here no longer exist on this path: Stock's
    /// answer arrives later through the outcome consumer, and a Catalog failure happens before the key
    /// is claimed at all. The column stays — dropping it is a schema change that belongs with the rest
    /// of the dead-code cleanup, and it still distinguishes a completed claim from an in-flight one.
    /// </para>
    /// </remarks>
    private async Task<Order> WriteAsync(Order order, OrderIdempotencyKey claim)
    {
        claim.Complete(order.Id, StatusCodes.Status202Accepted);

        db.Orders.Add(order);

        // Written here rather than published here, so the event joins the order's transaction and the
        // two cannot disagree. OutboxPublisher drains it afterwards.
        db.OutboxMessages.Add(CreateOrderPlacedMessage(order));

        await db.SaveChangesAsync(NotCancellable);

        return order;
    }

    /// <summary>
    /// Builds the outbox row for a placed order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written for every accepted order, unconditionally. That was not true when the outbox was first
    /// added: reservation happened synchronously before this point, so only a <c>Confirmed</c> order had
    /// anything for a consumer to do and the row was wrapped in an <c>if</c>. Reservation now happens
    /// <i>because of</i> this row, so the condition is not merely unnecessary but inverted — gating it
    /// on a settled status would mean never publishing anything.
    /// </para>
    /// <para>
    /// The traceparent is captured here rather than in the publisher because this is the last point at
    /// which there is a request to capture it from. The publisher runs on a background loop with no
    /// ambient <see cref="Activity"/>, so reading it there would silently publish an untraceable
    /// message — and the gap would only be visible in a trace nobody thought to look at.
    /// </para>
    /// </remarks>
    private static OutboxMessage CreateOrderPlacedMessage(Order order)
    {
        var messageId = Guid.NewGuid();

        return OutboxMessage.Create(
            messageId,
            OrderEventTopology.Exchange,
            OrderEventTopology.OrderPlacedRoutingKey,
            JsonSerializer.Serialize(OrderPlaced.From(order, messageId), OrderPlaced.JsonOptions),
            Activity.Current?.Id,
            DateTimeOffset.UtcNow);
    }

    private sealed record WantedLine(Guid ProductId, int Quantity);

    private sealed record ResolvedLine(CatalogProduct Product, int Quantity);
}
