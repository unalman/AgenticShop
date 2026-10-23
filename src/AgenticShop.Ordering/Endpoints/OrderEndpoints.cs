using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.Errors;
using AgenticShop.Shared.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Ordering.Endpoints;

/// <summary>
/// Order placement and retrieval. Two routes, and deliberately no more.
/// </summary>
/// <remarks>
/// <para>
/// There is no list endpoint. A status-filtered list would be the operator's way into the orders that
/// need attention, but it would need paging to be safe, and Catalog's paging is explicitly not house
/// style — Stock has no list endpoint for the same reason. The 409 and 502 bodies carry the order id,
/// and <c>GET /api/v1/orders/{id}</c> returns the whole record, so a client can always reach what it
/// placed. Finding orders that were accepted and never settled is a different need — an operator's
/// query, not a client's — and until a read model exists it is a direct one:
/// <c>status = 'Pending' AND updated_at_utc IS NULL</c>.
/// </para>
/// <para>
/// There is no cancel, and no route that mutates a persisted order. Placement is still the only
/// <i>HTTP</i> write path, but it is no longer the only writer: <c>StockOutcomeConsumer</c> advances a
/// <see cref="OrderStatus.Pending"/> order to a terminal one, which is why <c>Pending</c> is now
/// persisted and why <see cref="Order.UpdatedAtUtc"/> exists.
/// </para>
/// </remarks>
public static class OrderEndpoints
{
    /// <summary>
    /// Required on <c>POST /orders</c>. A missing header is a binding failure, so the framework
    /// answers 400 through the shared <c>BadHttpRequestException</c> arm rather than through
    /// anything written here.
    /// </summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .AddEndpointFilter<DataAnnotationValidationFilter>();

        orders.MapPost("/", PlaceAsync)
            .WithName("PlaceOrder")
            .WithSummary(
                "Accepts an order: resolves every line against Catalog, writes the order as Pending " +
                "and queues the event that gets it fulfilled. Requires an Idempotency-Key header; " +
                "repeating a key replays the first attempt instead of placing again, and reports the " +
                "order's current state. 202 while the order is still Pending, 200 on a replay once it " +
                "has settled. 409 when a product cannot be ordered or the key is in use; 502 when " +
                "Catalog let the placement down. Whether stock could be held is not answered here — " +
                "poll GET /api/v1/orders/{id}.")
            .Produces<OrderResponse>(StatusCodes.Status202Accepted)
            .Produces<OrderResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        orders.MapGet("/{id:guid}", GetAsync)
            .WithName("GetOrder")
            .WithSummary("Gets one order with its lines.")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> PlaceAsync(
        [FromHeader(Name = IdempotencyKeyHeader)] string idempotencyKey,
        CreateOrderRequest request,
        OrderingDbContext db,
        OrderPlacer placer,
        CancellationToken cancellationToken)
    {
        // Checked here rather than inside the placer because the replay has to build an IResult, and
        // the placer's contract is to return a placed order. This is a fast path, not the guarantee:
        // the placer's claim on the primary key is what actually prevents a second placement, so a
        // duplicate that slips past this read is still refused downstream.
        var replayed = await TryReplayAsync(db, idempotencyKey, cancellationToken);

        if (replayed is not null)
        {
            return replayed;
        }

        // Every non-success outcome is an exception classified by OrderingExceptionHandler, so the
        // endpoint has one job: turn an accepted order into a 202 with a Location to poll.
        var order = await placer.PlaceAsync(request, idempotencyKey, cancellationToken);

        return Accepted(order);
    }

    /// <summary>
    /// Returns the recorded response for a key that has already been used, or null if the key is
    /// fresh and the placement should proceed.
    /// </summary>
    private static async Task<IResult?> TryReplayAsync(
        OrderingDbContext db,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var claim = await db.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == idempotencyKey, cancellationToken);

        if (claim is null)
        {
            return null;
        }

        // Claimed but never completed: a placement is in flight, or the process died holding the
        // claim. The two cannot be told apart from here and the client's correct action is the same
        // either way — wait, then retry this key. Fail closed: refusing is safe, placing again is not.
        if (claim.OrderId is not { } orderId)
        {
            throw new IdempotencyKeyInUseException(idempotencyKey);
        }

        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        // The key and the order commit in one transaction, so a completed key with no order is
        // unreachable through any path this assembly contains. A server fault, not a client one.
        if (order is null)
        {
            throw new InvalidOperationException(
                $"Idempotency key '{idempotencyKey}' is recorded as completed for order {orderId}, " +
                "but no such order exists.");
        }

        return Replay(order);
    }

    /// <summary>
    /// Rebuilds the response for a key that has already been used, from the order's current state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status is <b>derived</b> rather than replayed verbatim, which is a deliberate weakening of
    /// the usual idempotency contract and a direct consequence of the order no longer being write-once.
    /// The first attempt answers 202 because the order is <c>Pending</c>; a retry minutes later finds it
    /// <c>Confirmed</c> or <c>Failed</c>, and repeating 202 would tell the client that work is still in
    /// flight when it finished long ago. So a replay reports where the order actually is — 202 while
    /// <c>Pending</c>, 200 once settled. What the key still guarantees exactly is the part that matters:
    /// a retry never places a second order.
    /// </para>
    /// <para>
    /// The claim's stored status code is no longer read. It is written as a constant 202 because that is
    /// the only successful outcome now, and the column survives only to distinguish a completed claim
    /// from an in-flight one — which <see cref="OrderIdempotencyKey.OrderId"/> already does. Dropping it
    /// is a schema change that belongs with the dead-code cleanup rather than with this flip.
    /// </para>
    /// <para>
    /// There is no failure branch any more. A 409 for refused stock and a 502 for a downstream fault
    /// used to complete a claim with that status and be replayed from it; neither can happen after the
    /// claim is taken, because Stock's answer arrives asynchronously and Catalog is resolved before the
    /// key is claimed at all.
    /// </para>
    /// </remarks>
    private static IResult Replay(Order order) => order.IsTerminal
        ? Results.Ok(OrderResponse.From(order))
        : Accepted(order);

    private static IResult Accepted(Order order)
        => Results.Accepted($"/api/v1/orders/{order.Id}", OrderResponse.From(order));

    private static async Task<IResult> GetAsync(
        Guid id,
        OrderingDbContext db,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null
            ? Results.NotFound()
            : Results.Ok(OrderResponse.From(order));
    }
}
