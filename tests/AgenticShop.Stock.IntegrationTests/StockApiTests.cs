using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgenticShop.Shared.Middleware;
using AgenticShop.Stock.Contracts;
using AgenticShop.Stock.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgenticShop.Stock.IntegrationTests;

[Collection("Stock API")]
public sealed class StockApiTests(StockApiFixture fixture) : IAsyncLifetime
{
    private const string StockUrl = "/api/v1/stock";

    private HttpClient Client => fixture.Client;

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_Returns200()
    {
        var response = await Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetStock_Returns201WithLocationAndNothingReserved()
    {
        var productId = Guid.NewGuid();

        var response = await Client.PostAsJsonAsync(StockUrl, new SetStockRequest(productId, 25));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be($"{StockUrl}/{productId}");

        var created = await ReadJsonAsync<StockResponse>(response);

        created.ProductId.Should().Be(productId);
        created.QuantityOnHand.Should().Be(25);
        created.Reserved.Should().Be(0, "provisioning stock holds nothing");
        created.Available.Should().Be(25);
    }

    [Fact]
    public async Task SetStock_ForTheSameProductTwice_Returns409NamingTheProduct()
    {
        var productId = Guid.NewGuid();
        await CreateStockAsync(productId, 10);

        var response = await Client.PostAsJsonAsync(StockUrl, new SetStockRequest(productId, 5));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await ReadJsonAsync<ProblemDetails>(response);

        problem.Detail.Should().Be("A stock record for that product already exists.");
    }

    [Fact]
    public async Task SetStock_WithAnEmptyProductId_Returns400()
    {
        // No DataAnnotation expresses "non-default Guid", so this is the domain guard acting as
        // the backstop — and it must still be a 4xx, never a 500.
        var response = await Client.PostAsJsonAsync(StockUrl, new SetStockRequest(Guid.Empty, 10));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync<ProblemDetails>(response)).Detail.Should().BeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task SetStock_WithANegativeCount_Returns400ValidationProblem(int quantity)
    {
        var response = await Client.PostAsJsonAsync(StockUrl, new SetStockRequest(Guid.NewGuid(), quantity));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync<HttpValidationProblemDetails>(response)).Errors.Should().ContainKey("QuantityOnHand");
    }

    [Fact]
    public async Task SetStock_AboveTheDomainMaximum_Returns400ValidationProblem()
    {
        var response = await Client.PostAsJsonAsync(
            StockUrl,
            new SetStockRequest(Guid.NewGuid(), StockItem.MaxQuantity + 1));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync<HttpValidationProblemDetails>(response)).Errors.Should().ContainKey("QuantityOnHand");
    }

    [Fact]
    public async Task GetStock_ReturnsThePosition()
    {
        var productId = Guid.NewGuid();
        await CreateStockAsync(productId, 7);

        var response = await Client.GetAsync($"{StockUrl}/{productId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stock = await ReadJsonAsync<StockResponse>(response);

        stock.ProductId.Should().Be(productId);
        stock.QuantityOnHand.Should().Be(7);
        stock.Available.Should().Be(7);
    }

    [Fact]
    public async Task GetStock_ForAnUnknownProduct_Returns404()
    {
        var response = await Client.GetAsync($"{StockUrl}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateStock_ReplacesThePhysicalCount()
    {
        var productId = Guid.NewGuid();
        await CreateStockAsync(productId, 10);

        var response = await Client.PutAsJsonAsync($"{StockUrl}/{productId}", new UpdateStockRequest(30));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await ReadJsonAsync<StockResponse>(response);

        updated.QuantityOnHand.Should().Be(30);
        updated.Available.Should().Be(30);
    }

    [Fact]
    public async Task UpdateStock_BelowWhatIsReserved_Returns400()
    {
        var productId = Guid.NewGuid();
        await CreateStockAsync(productId, 10);
        await ReserveAsync(productId, Guid.NewGuid(), 6);

        var response = await Client.PutAsJsonAsync($"{StockUrl}/{productId}", new UpdateStockRequest(5));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync<ProblemDetails>(response)).Detail.Should().BeNull(
            "the domain message names internal parameters and is formatted with the server culture");
    }

    [Fact]
    public async Task UpdateStock_DownToExactlyWhatIsReserved_Succeeds()
    {
        var productId = Guid.NewGuid();
        await CreateStockAsync(productId, 10);
        await ReserveAsync(productId, Guid.NewGuid(), 6);

        var response = await Client.PutAsJsonAsync($"{StockUrl}/{productId}", new UpdateStockRequest(6));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await ReadJsonAsync<StockResponse>(response);

        updated.QuantityOnHand.Should().Be(6);
        updated.Available.Should().Be(0);
    }

    [Fact]
    public async Task UpdateStock_ForAnUnknownProduct_Returns404()
    {
        var response = await Client.PutAsJsonAsync($"{StockUrl}/{Guid.NewGuid()}", new UpdateStockRequest(1));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Binding failures happen before the validation filter runs, so only the exception handler
    // can classify them. Every one of these returned 500 in Catalog until that was fixed.

    [Fact]
    public async Task MalformedJson_Returns400ProblemJson()
    {
        var response = await Client.PostAsync(StockUrl, RawJson("{"));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task EmptyBody_Returns400ProblemJson()
    {
        var response = await Client.PostAsync(StockUrl, RawJson(string.Empty));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task NonNumericQuantity_Returns400ProblemJson()
    {
        var response = await Client.PostAsync(
            StockUrl,
            RawJson("""{"productId":"11111111-1111-1111-1111-111111111111","quantityOnHand":"abc"}"""));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task Responses_EchoAnInboundCorrelationId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{StockUrl}/{Guid.NewGuid()}");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "stock-corr-42");

        var response = await Client.SendAsync(request);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)
            .Single()
            .Should().Be("stock-corr-42");
    }

    [Fact]
    public async Task Responses_ReplaceAnUnusableCorrelationIdRatherThanEchoingIt()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{StockUrl}/{Guid.NewGuid()}");

        // TryAddWithoutValidation, because the point is sending what a well-behaved client never would.
        request.Headers.TryAddWithoutValidation(
            CorrelationIdMiddleware.HeaderName,
            new string('a', CorrelationIdMiddleware.MaxLength + 1));

        var response = await Client.SendAsync(request);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)
            .Single()
            .Should().MatchRegex("^[0-9a-f]{32}$");
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

    internal static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>();

        value.Should().NotBeNull("the response should carry a JSON body");

        return value!;
    }

    internal async Task<StockResponse> CreateStockAsync(Guid productId, int quantityOnHand)
    {
        var response = await Client.PostAsJsonAsync(StockUrl, new SetStockRequest(productId, quantityOnHand));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the stock record should have been created");

        return await ReadJsonAsync<StockResponse>(response);
    }

    internal async Task<ReservationResponse> ReserveAsync(Guid productId, Guid orderId, int quantity)
    {
        var response = await Client.PostAsJsonAsync(
            $"{StockUrl}/{productId}/reservations",
            new ReserveStockRequest(orderId, quantity));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the reservation should have been created");

        return await ReadJsonAsync<ReservationResponse>(response);
    }
}
