namespace AgenticShop.Ordering.Domain;

/// <summary>
/// Thrown when a requested product cannot be ordered at all: Catalog answered 404, which covers
/// both "never existed" and "soft-deleted", because Catalog's query filter hides inactive products.
/// </summary>
/// <remarks>
/// A distinct type because the handler maps <see cref="ArgumentException"/> to 400 and this is a
/// 409: the request is well-formed and conflicts with the current state of the catalog, and a retry
/// after the product is reinstated can succeed. That is the same reasoning Stock applies to
/// insufficient stock.
/// </remarks>
public sealed class ProductUnavailableException(Guid productId)
    : Exception($"Product {productId} is not orderable.")
{
    public Guid ProductId { get; } = productId;
}
