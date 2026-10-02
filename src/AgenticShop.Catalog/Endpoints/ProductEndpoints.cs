using AgenticShop.Catalog.Contracts;
using AgenticShop.Catalog.Data;
using AgenticShop.Catalog.Domain;
using AgenticShop.Shared.Validation;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Catalog.Endpoints;

public static class ProductEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/v1/products")
            .WithTags("Products")
            .AddEndpointFilter<DataAnnotationValidationFilter>();

        products.MapGet("/", ListAsync)
            .WithName("ListProducts")
            .WithSummary("Lists active products, ordered by SKU.")
            .Produces<ProductPage>();

        products.MapGet("/{id:guid}", GetAsync)
            .WithName("GetProduct")
            .WithSummary("Gets a single product by id.")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        products.MapPost("/", CreateAsync)
            .WithName("CreateProduct")
            .WithSummary("Creates a product.")
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        products.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateProduct")
            .WithSummary("Updates a product's name, description and price. SKU is immutable.")
            .Produces<ProductResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        products.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteProduct")
            .WithSummary("Soft-deletes a product by deactivating it.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListAsync(
        CatalogDbContext db,
        CancellationToken cancellationToken,
        int page = 1,
        int size = DefaultPageSize,
        bool includeInactive = false)
    {
        page = Math.Max(page, 1);
        size = Math.Clamp(size, 1, MaxPageSize);

        var query = includeInactive
            ? db.Products.IgnoreQueryFilters()
            : db.Products;

        var totalCount = await query.CountAsync(cancellationToken);

        // Ordered by SKU rather than Name: SKU is unique, so the paging window is
        // stable. Paging over a non-unique column can skip or repeat rows.
        var products = await query
            .AsNoTracking()
            .OrderBy(p => p.Sku)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var items = products.Select(ProductResponse.From).ToList();

        return Results.Ok(new ProductPage(items, page, size, totalCount));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return product is null
            ? Results.NotFound()
            : Results.Ok(ProductResponse.From(product));
    }

    private static async Task<IResult> CreateAsync(
        CreateProductRequest request,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var product = Product.Create(
            request.Sku,
            request.Name,
            request.Description,
            request.Price,
            request.Currency);

        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/products/{product.Id}", ProductResponse.From(product));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateProductRequest request,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return Results.NotFound();
        }

        product.Update(request.Name, request.Description, request.Price);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ProductResponse.From(product));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return Results.NotFound();
        }

        product.Deactivate();
        await db.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}
