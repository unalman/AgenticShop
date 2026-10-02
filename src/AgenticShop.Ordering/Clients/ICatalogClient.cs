namespace AgenticShop.Ordering.Clients;

/// <summary>
/// The seam over Catalog. The one abstraction this project justifies in advance, because it is a
/// real network boundary: it is what the integration tests fake, and it is what a broker-based
/// implementation would swap into in Phase 2.
/// </summary>
public interface ICatalogClient
{
    /// <summary>
    /// Returns the product, or <see langword="null"/> when Catalog does not have an orderable one.
    /// </summary>
    /// <remarks>
    /// A single null covers both "never existed" and "soft-deleted", because Catalog's query filter
    /// hides inactive products and answers 404 for them. Ordering has no reason to distinguish the
    /// two: neither can be ordered.
    /// </remarks>
    /// <exception cref="DownstreamServiceException">Catalog returned a 5xx, timed out, or was unreachable.</exception>
    Task<CatalogProduct?> GetProductAsync(Guid productId, CancellationToken cancellationToken);
}
