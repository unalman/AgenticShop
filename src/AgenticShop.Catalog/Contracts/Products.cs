using System.ComponentModel.DataAnnotations;
using AgenticShop.Catalog.Domain;
using AgenticShop.Shared.Contracts;

namespace AgenticShop.Catalog.Contracts;

public sealed record CreateProductRequest(
    [property: Required]
    [property: StringLength(Product.SkuMaxLength, MinimumLength = 1)]
    string Sku,

    [property: Required]
    [property: StringLength(Product.NameMaxLength, MinimumLength = 1)]
    string Name,

    [property: StringLength(Product.DescriptionMaxLength)]
    string? Description,

    [property: Range(typeof(decimal), "0", "9999999999999999")]
    decimal Price,

    [property: StringLength(Product.CurrencyLength, MinimumLength = Product.CurrencyLength)]
    [property: RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "Currency must be a three-letter ISO 4217 code such as USD.")]
    string? Currency) : IRequestContract;

public sealed record UpdateProductRequest(
    [property: Required]
    [property: StringLength(Product.NameMaxLength, MinimumLength = 1)]
    string Name,

    [property: StringLength(Product.DescriptionMaxLength)]
    string? Description,

    [property: Range(typeof(decimal), "0", "9999999999999999")]
    decimal Price) : IRequestContract;

public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    decimal Price,
    string Currency,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static ProductResponse From(Product product) => new(
        product.Id,
        product.Sku,
        product.Name,
        product.Description,
        product.Price,
        product.Currency,
        product.IsActive,
        product.CreatedAtUtc,
        product.UpdatedAtUtc);
}

public sealed record ProductPage(
    IReadOnlyList<ProductResponse> Items,
    int Page,
    int Size,
    int TotalCount);
