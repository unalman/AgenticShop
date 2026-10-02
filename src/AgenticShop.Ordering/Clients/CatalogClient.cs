using System.Net;
using System.Net.Http.Json;

namespace AgenticShop.Ordering.Clients;

/// <summary>
/// Typed client for Catalog. Registered with a base address from
/// <c>Services:Catalog:BaseUrl</c>, so nothing here knows a port.
/// </summary>
public sealed class CatalogClient(HttpClient http) : DownstreamClient(http, "Catalog"), ICatalogClient
{
    public async Task<CatalogProduct?> GetProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        const string operation = nameof(GetProductAsync);

        // Paths are rooted rather than relative: a rooted path resolves against the authority and
        // ignores any path on the base address, so a base URL configured with a prefix cannot
        // silently rewrite the route.
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/products/{productId}"),
            operation,
            cancellationToken);

        // Catalog's query filter hides soft-deleted products, so 404 covers "gone" as well as
        // "never existed". Ordering has no reason to distinguish them: neither can be ordered.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.IsSuccessStatusCode)
        {
            var product = await response.Content.ReadFromJsonAsync<CatalogProduct>(cancellationToken);

            return product ?? throw MalformedBody(Service, operation);
        }

        throw Unexpected(Service, operation, response);
    }
}
