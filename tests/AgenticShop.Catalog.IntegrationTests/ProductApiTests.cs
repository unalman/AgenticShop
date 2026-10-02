using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgenticShop.Catalog.Contracts;
using AgenticShop.Catalog.Domain;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgenticShop.Catalog.IntegrationTests;

[Collection("Catalog API")]
public sealed class ProductApiTests(CatalogApiFixture fixture) : IAsyncLifetime
{
    private const string ProductsUrl = "/api/v1/products";

    /// <summary>The format CorrelationIdMiddleware mints when it rejects an inbound value.</summary>
    private const string MintedCorrelationIdPattern = "^[0-9a-f]{32}$";

    private HttpClient Client => fixture.Client;

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateProduct_Returns201WithLocationAndBody()
    {
        var request = new CreateProductRequest("SKU-1", "Espresso Machine", "Dual boiler.", 199.99m, "USD");

        var response = await Client.PostAsJsonAsync(ProductsUrl, request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await ReadJsonAsync<ProductResponse>(response);

        created.Id.Should().NotBeEmpty();
        created.Sku.Should().Be("SKU-1");
        created.Name.Should().Be("Espresso Machine");
        created.Description.Should().Be("Dual boiler.");
        created.Price.Should().Be(199.99m);
        created.Currency.Should().Be("USD");
        created.IsActive.Should().BeTrue();

        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be($"{ProductsUrl}/{created.Id}");
    }

    [Fact]
    public async Task CreateProduct_DefaultsCurrencyToUsdWhenOmitted()
    {
        var response = await Client.PostAsJsonAsync(
            ProductsUrl,
            new CreateProductRequest("SKU-1", "Espresso Machine", null, 10m, null));

        (await ReadJsonAsync<ProductResponse>(response)).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task CreateProduct_WithDuplicateSku_Returns409Conflict()
    {
        await CreateProductAsync("SKU-DUPLICATE");

        var response = await Client.PostAsJsonAsync(
            ProductsUrl,
            new CreateProductRequest("SKU-DUPLICATE", "Another product", null, 5m, null));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await ReadJsonAsync<ProblemDetails>(response);

        problem.Status.Should().Be(409);
        problem.Extensions.Should().ContainKey("correlationId");
    }

    [Fact]
    public async Task CreateProduct_WithNegativePrice_Returns400ValidationProblem()
    {
        var response = await Client.PostAsJsonAsync(
            ProductsUrl,
            new CreateProductRequest("SKU-1", "Espresso Machine", null, -1m, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync<HttpValidationProblemDetails>(response);

        problem.Errors.Should().ContainKey("Price");
    }

    [Fact]
    public async Task CreateProduct_WithOverlongSku_Returns400ValidationProblem()
    {
        // Regression: minimal APIs do not validate body DTOs on their own, so before
        // DataAnnotationValidationFilter existed this value reached PostgreSQL, which
        // rejected the insert and surfaced to the caller as a 500.
        var response = await Client.PostAsJsonAsync(
            ProductsUrl,
            new CreateProductRequest(new string('A', Product.SkuMaxLength + 1), "Espresso Machine", null, 10m, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync<HttpValidationProblemDetails>(response);

        problem.Errors.Should().ContainKey("Sku");
    }

    // The four cases below all fail during parameter binding, before the validation filter
    // runs, and arrive as BadHttpRequestException. Every one of them returned 500 until the
    // handler learned to read that exception's StatusCode. They are trivially triggerable by
    // any client, so misclassifying them inflates 5xx metrics and floods the error log.

    [Fact]
    public async Task MalformedJsonBody_Returns400ProblemJson()
    {
        var response = await Client.PostAsync(ProductsUrl, RawJson("{"));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task EmptyBody_Returns400ProblemJson()
    {
        var response = await Client.PostAsync(ProductsUrl, RawJson(string.Empty));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task NonNumericPriceField_Returns400ProblemJson()
    {
        var response = await Client.PostAsync(
            ProductsUrl,
            RawJson("""{"sku":"SKU-1","name":"Espresso Machine","price":"abc"}"""));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task NonNumericQueryParameter_Returns400ProblemJson()
    {
        // Binding failures are not limited to the body: `size` is an int route parameter
        // with no DTO in front of it, so no validation filter could ever catch this.
        var response = await Client.GetAsync($"{ProductsUrl}?size=abc");

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task GetProduct_ReturnsTheProduct()
    {
        var created = await CreateProductAsync("SKU-1");

        var response = await Client.GetAsync($"{ProductsUrl}/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync<ProductResponse>(response)).Sku.Should().Be("SKU-1");
    }

    [Fact]
    public async Task GetProduct_WhenMissing_Returns404()
    {
        var response = await Client.GetAsync($"{ProductsUrl}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListProducts_OrdersBySku_AndReportsPaging()
    {
        await CreateProductAsync("SKU-C");
        await CreateProductAsync("SKU-A");
        await CreateProductAsync("SKU-B");

        var page = await Client.GetFromJsonAsync<ProductPage>($"{ProductsUrl}?page=1&size=2");

        page.Should().NotBeNull();
        page!.TotalCount.Should().Be(3);
        page.Page.Should().Be(1);
        page.Size.Should().Be(2);
        page.Items.Select(i => i.Sku).Should().Equal("SKU-A", "SKU-B");

        var secondPage = await Client.GetFromJsonAsync<ProductPage>($"{ProductsUrl}?page=2&size=2");

        secondPage!.Items.Select(i => i.Sku).Should().Equal("SKU-C");
    }

    [Fact]
    public async Task ListProducts_ClampsAnOversizedPageRequest()
    {
        var page = await Client.GetFromJsonAsync<ProductPage>($"{ProductsUrl}?page=0&size=1000");

        page!.Page.Should().Be(1);
        page.Size.Should().Be(100);
    }

    [Fact]
    public async Task UpdateProduct_ChangesMutableFields_ButNeverTheSku()
    {
        var created = await CreateProductAsync("SKU-1", "Espresso Machine", 199.99m);

        var response = await Client.PutAsJsonAsync(
            $"{ProductsUrl}/{created.Id}",
            new UpdateProductRequest("Espresso Machine Pro", "Upgraded.", 249.5m));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await ReadJsonAsync<ProductResponse>(response);

        updated.Sku.Should().Be("SKU-1");
        updated.Name.Should().Be("Espresso Machine Pro");
        updated.Description.Should().Be("Upgraded.");
        updated.Price.Should().Be(249.5m);
        updated.UpdatedAtUtc.Should().BeOnOrAfter(created.UpdatedAtUtc);
    }

    [Fact]
    public async Task UpdateProduct_WhenMissing_Returns404()
    {
        var response = await Client.PutAsJsonAsync(
            $"{ProductsUrl}/{Guid.NewGuid()}",
            new UpdateProductRequest("Name", null, 1m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProduct_Returns204_AndHidesTheProductFromTheDefaultList()
    {
        var created = await CreateProductAsync("SKU-1");

        var delete = await Client.DeleteAsync($"{ProductsUrl}/{created.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client.GetAsync($"{ProductsUrl}/{created.Id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await Client.GetFromJsonAsync<ProductPage>(ProductsUrl))!.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteProduct_WhenMissing_Returns404()
    {
        var response = await Client.DeleteAsync($"{ProductsUrl}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListProducts_WithIncludeInactive_ShowsSoftDeletedProducts()
    {
        var created = await CreateProductAsync("SKU-1");
        await Client.DeleteAsync($"{ProductsUrl}/{created.Id}");

        var page = await Client.GetFromJsonAsync<ProductPage>($"{ProductsUrl}?includeInactive=true");

        page!.TotalCount.Should().Be(1);
        page.Items.Single().IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Responses_EchoAnInboundCorrelationId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProductsUrl);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "correlation-abc-123");

        var response = await Client.SendAsync(request);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)
            .Single()
            .Should().Be("correlation-abc-123");
    }

    [Fact]
    public async Task Responses_MintACorrelationIdWhenNoneIsSupplied()
    {
        var response = await Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values)
            .Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("order_9f2c.1")]
    public async Task ACorrelationIdWithinTheAllowedCharset_IsEchoedUnchanged(string inbound)
        => (await SendWithCorrelationIdAsync(inbound)).Should().Be(inbound);

    [Fact]
    public async Task AnOverLongCorrelationId_IsReplacedRatherThanEchoed()
    {
        var hostile = new string('a', CorrelationIdMiddleware.MaxLength + 1);

        var echoed = await SendWithCorrelationIdAsync(hostile);

        echoed.Should().NotBe(hostile);
        echoed.Should().MatchRegex(MintedCorrelationIdPattern);
    }

    [Theory]
    [InlineData("contains a space")]
    [InlineData("semi;colon")]
    public async Task ACorrelationIdWithDisallowedCharacters_IsReplaced(string inbound)
    {
        // The value would otherwise be echoed into a response header, embedded in the
        // ProblemDetails body and written to every log line for the request.
        var echoed = await SendWithCorrelationIdAsync(inbound);

        echoed.Should().NotBe(inbound);
        echoed.Should().MatchRegex(MintedCorrelationIdPattern);
    }

    [Fact]
    public async Task UpdateProduct_WithNegativePrice_Returns400ValidationProblem()
    {
        // L27: the PUT route declares ProducesValidationProblem() but nothing exercised it.
        var created = await CreateProductAsync("SKU-1");

        var response = await Client.PutAsJsonAsync(
            $"{ProductsUrl}/{created.Id}",
            new UpdateProductRequest("Espresso Machine Pro", null, -1m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync<HttpValidationProblemDetails>(response);

        problem.Errors.Should().ContainKey("Price");
    }

    [Fact]
    public async Task UpdateProduct_WithOverlongName_Returns400ValidationProblem()
    {
        var created = await CreateProductAsync("SKU-1");

        var response = await Client.PutAsJsonAsync(
            $"{ProductsUrl}/{created.Id}",
            new UpdateProductRequest(new string('N', Product.NameMaxLength + 1), null, 1m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ReadJsonAsync<HttpValidationProblemDetails>(response)).Errors.Should().ContainKey("Name");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("US1")]
    [InlineData("US")]
    public async Task CreateProduct_WithANonIsoCurrencyCode_Returns400ValidationProblem(string currency)
    {
        // L1: a bare length check used to accept these and persist them to the currency column.
        var response = await Client.PostAsJsonAsync(
            ProductsUrl,
            new CreateProductRequest("SKU-1", "Espresso Machine", null, 10m, currency));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ReadJsonAsync<HttpValidationProblemDetails>(response)).Errors.Should().ContainKey("Currency");
    }

    private async Task<string> SendWithCorrelationIdAsync(string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProductsUrl);

        // TryAddWithoutValidation, because the point of the test is sending values that a
        // well-behaved client would never produce.
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, value);

        var response = await Client.SendAsync(request);

        return response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
    }

    private static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task AssertBadRequestAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await ReadJsonAsync<ProblemDetails>(response);

        problem.Status.Should().Be(400);
        problem.Title.Should().Be("Bad request.");
        problem.Detail.Should().BeNull("internal exception text must not reach the client");
        problem.Extensions.Should().ContainKey("correlationId");
    }

    private async Task<ProductResponse> CreateProductAsync(
        string sku,
        string name = "Espresso Machine",
        decimal price = 199.99m)
    {
        var response = await Client.PostAsJsonAsync(
            ProductsUrl,
            new CreateProductRequest(sku, name, null, price, null));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the product should have been created");

        return await ReadJsonAsync<ProductResponse>(response);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>();

        value.Should().NotBeNull("the response should carry a JSON body");

        return value!;
    }
}
