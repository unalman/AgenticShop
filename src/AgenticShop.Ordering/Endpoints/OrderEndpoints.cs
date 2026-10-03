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
/// There is no list endpoint. A status-filtered list would be the operator's way into
/// <see cref="OrderStatus.PartiallyConfirmed"/> orders, but it would need paging to be safe, and
/// Catalog's paging is explicitly not house style — Stock has no list endpoint for the same reason.
/// Phase 0 does not need one: the 409 and 502 bodies carry the order id, and
/// <c>GET /api/v1/orders/{id}</c> returns the whole record. Phase 2's saga will need a work queue
/// rather than a list, which is a different thing.
/// </para>
/// <para>
/// There is no cancel, and no route that mutates a persisted order. Placement is the only write
/// path, which is what makes the row write-once and <see cref="OrderStatus.Pending"/> unpersisted.
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
                "Places an order: resolves every line against Catalog, holds and settles it in " +
                "Stock, and writes the order once. Requires an Idempotency-Key header; repeating a " +
                "key replays the first attempt instead of placing again. 409 when a product cannot " +
                "be ordered, stock was refused, or the key is already in use; 502 when a downstream " +
                "service let the placement down.")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
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
        // endpoint has one job: turn a placed order into a 201 with a Location.
        var order = await placer.PlaceAsync(request, idempotencyKey, cancellationToken);

        return Created(order);
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

        return Replay(claim.StatusCode!.Value, order);
    }

    /// <summary>
    /// Rebuilds the recorded response. The status is replayed verbatim; the body comes from the
    /// order row rather than a stored payload.
    /// </summary>
    /// <remarks>
    /// For a 201 that is byte-identical to the original, because the row is write-once and
    /// <see cref="OrderResponse.From"/> is a pure function of it. For a failure the detail text
    /// differs from the first attempt's and says that this is a replay — a deliberate deviation,
    /// because storing serialised response bodies would create a second source of truth that could
    /// drift from the row it describes. The status is what a client branches on, and that is exact.
    /// </remarks>
    private static IResult Replay(int statusCode, Order order) => statusCode switch
    {
        StatusCodes.Status201Created => Created(order),

        _ => Results.Problem(
            title: statusCode == StatusCodes.Status502BadGateway
                ? OrderingExceptionHandler.DownstreamFailureTitle
                : OrderingExceptionHandler.ConflictFailureTitle,
            detail: "This is the recorded outcome of an earlier attempt made with the same " +
                "idempotency key. No new order was placed.",
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["orderId"] = order.Id
            })
    };

    private static IResult Created(Order order)
        => Results.Created($"/api/v1/orders/{order.Id}", OrderResponse.From(order));

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
