using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Shared.Validation;
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
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .AddEndpointFilter<DataAnnotationValidationFilter>();

        orders.MapPost("/", PlaceAsync)
            .WithName("PlaceOrder")
            .WithSummary(
                "Places an order: resolves every line against Catalog, holds and settles it in " +
                "Stock, and writes the order once. 409 when a product cannot be ordered or stock " +
                "was refused, 502 when a downstream service let the placement down.")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
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
        CreateOrderRequest request,
        OrderPlacer placer,
        CancellationToken cancellationToken)
    {
        // Every non-success outcome is an exception classified by OrderingExceptionHandler, so the
        // endpoint has one job: turn a placed order into a 201 with a Location.
        var order = await placer.PlaceAsync(request, cancellationToken);

        return Results.Created($"/api/v1/orders/{order.Id}", OrderResponse.From(order));
    }

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
