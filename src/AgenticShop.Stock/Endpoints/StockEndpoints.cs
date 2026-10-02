using AgenticShop.Shared.Validation;
using AgenticShop.Stock.Contracts;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Stock.Endpoints;

public static class StockEndpoints
{
    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var stock = app.MapGroup("/api/v1/stock")
            .WithTags("Stock")
            .AddEndpointFilter<DataAnnotationValidationFilter>();

        stock.MapGet("/{productId:guid}", GetAsync)
            .WithName("GetStock")
            .WithSummary("Gets the stock position for a product.")
            .Produces<StockResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        stock.MapPost("/", CreateAsync)
            .WithName("SetStock")
            .WithSummary("Provisions the stock record for a product.")
            .Produces<StockResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        stock.MapPut("/{productId:guid}", UpdateAsync)
            .WithName("UpdateStock")
            .WithSummary("Replaces the physical count. Cannot be set below what is reserved.")
            .Produces<StockResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetAsync(
        Guid productId,
        StockDbContext db,
        CancellationToken cancellationToken)
    {
        var item = await db.StockItems
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProductId == productId, cancellationToken);

        return item is null
            ? Results.NotFound()
            : Results.Ok(StockResponse.From(item));
    }

    private static async Task<IResult> CreateAsync(
        SetStockRequest request,
        StockDbContext db,
        CancellationToken cancellationToken)
    {
        // ProductId is not resolved against Catalog. Stock is a leaf service: it accepts the
        // id and manages inventory for it. Validating it would mean an outbound call and a
        // Stock -> Catalog dependency, and in the real flow Ordering has already checked.
        var item = StockItem.Create(request.ProductId, request.QuantityOnHand);

        db.StockItems.Add(item);

        // A duplicate ProductId is left to the unique index rather than pre-checked: a
        // SELECT-then-INSERT test has a race window, the constraint does not.
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/stock/{item.ProductId}", StockResponse.From(item));
    }

    private static async Task<IResult> UpdateAsync(
        Guid productId,
        UpdateStockRequest request,
        StockDbContext db,
        CancellationToken cancellationToken)
    {
        var item = await db.StockItems
            .FirstOrDefaultAsync(s => s.ProductId == productId, cancellationToken);

        if (item is null)
        {
            return Results.NotFound();
        }

        item.SetQuantityOnHand(request.QuantityOnHand);

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(StockResponse.From(item));
    }
}
