using AgenticShop.Ordering.Clients;

namespace AgenticShop.Ordering.IntegrationTests.Fakes;

/// <summary>
/// Stands in for Catalog at the interface seam. Holds real products rather than canned responses, so
/// a test describes the catalog it wants instead of scripting a call sequence.
/// </summary>
public sealed class FakeCatalogClient : ICatalogClient
{
    /// <summary>How Catalog lets Ordering down, when it does.</summary>
    public enum Failure
    {
        None,

        /// <summary>A 5xx.</summary>
        ServerError,

        /// <summary>No response at all, which the client reports the same way.</summary>
        Timeout
    }

    private readonly Dictionary<Guid, CatalogProduct> _products = [];

    public Failure FailsWith { get; set; }

    /// <summary>Every product id asked for, in order — which is how "reserves nothing" is proven.</summary>
    public List<Guid> Requested { get; } = [];

    public FakeCatalogClient WithProduct(
        Guid productId,
        string name = "Widget",
        decimal price = 10.00m,
        string currency = "USD",
        bool isActive = true)
    {
        _products[productId] = new CatalogProduct(
            productId,
            $"SKU-{productId.ToString("N")[..8].ToUpperInvariant()}",
            name,
            price,
            currency,
            isActive);

        return this;
    }

    public void Reset()
    {
        _products.Clear();
        Requested.Clear();
        FailsWith = Failure.None;
    }

    public Task<CatalogProduct?> GetProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        Requested.Add(productId);

        if (FailsWith != Failure.None)
        {
            throw new DownstreamServiceException(
                "Catalog",
                nameof(GetProductAsync),
                FailsWith == Failure.ServerError ? 503 : null,
                innerException: null);
        }

        // Null is what the real client returns for a 404, which Catalog's query filter produces for
        // a soft-deleted product as well as an unknown one.
        return Task.FromResult(_products.TryGetValue(productId, out var product) ? product : null);
    }
}
