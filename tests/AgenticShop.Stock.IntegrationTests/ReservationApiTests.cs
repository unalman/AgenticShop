using System.Net;
using System.Net.Http.Json;
using AgenticShop.Stock.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// The reservation lifecycle over HTTP. Each test asserts the resulting counters, not just the
/// status code: a transition that returns 200 while leaving <c>reserved</c> wrong is the bug
/// that matters here.
/// </summary>
[Collection("Stock API")]
public sealed class ReservationApiTests(StockApiFixture fixture) : IAsyncLifetime
{
    private const string StockUrl = "/api/v1/stock";
    private const string ReservationsUrl = "/api/v1/reservations";

    private HttpClient Client => fixture.Client;

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reserve_HoldsQuantityWithoutShippingIt()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);

        var response = await ReserveAsync(productId, Guid.NewGuid(), 4);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var reservation = await StockApiTests.ReadJsonAsync<ReservationResponse>(response);

        reservation.Status.Should().Be("Pending");
        reservation.Quantity.Should().Be(4);
        reservation.ProductId.Should().Be(productId, "the caller needs the product back, but it is not stored on the row");
        response.Headers.Location!.ToString().Should().Be($"{ReservationsUrl}/{reservation.Id}");

        var stock = await GetStockAsync(productId);

        stock.Reserved.Should().Be(4);
        stock.QuantityOnHand.Should().Be(10, "reserving holds stock, it does not ship it");
        stock.Available.Should().Be(6);
    }

    [Fact]
    public async Task Reserve_OfEverythingAvailable_Succeeds()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 5);

        var response = await ReserveAsync(productId, Guid.NewGuid(), 5);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await GetStockAsync(productId)).Available.Should().Be(0);
    }

    [Fact]
    public async Task Reserve_OneUnitMoreThanAvailable_Returns409WithBothNumbers()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 5);

        var response = await ReserveAsync(productId, Guid.NewGuid(), 6);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await StockApiTests.ReadJsonAsync<ProblemDetails>(response);

        problem.Title.Should().Be("Conflict.");
        problem.Detail.Should().Be("Only 5 unit(s) are available; 6 were requested.");
    }

    [Fact]
    public async Task Reserve_WhenEverythingIsAlreadyHeld_Returns409()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 3);
        await ReserveAsync(productId, Guid.NewGuid(), 3);

        var response = await ReserveAsync(productId, Guid.NewGuid(), 1);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StockApiTests.ReadJsonAsync<ProblemDetails>(response))
            .Detail.Should().Be("Only 0 unit(s) are available; 1 were requested.");
    }

    [Fact]
    public async Task Reserve_ForAnUnknownProduct_Returns404()
    {
        var response = await ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), 1);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reserve_TwiceForTheSameOrderAndProduct_Returns409()
    {
        // The natural idempotency guard: a retried reserve must not hold the quantity twice.
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);

        (await ReserveAsync(productId, orderId, 2)).StatusCode.Should().Be(HttpStatusCode.Created);

        var retry = await ReserveAsync(productId, orderId, 2);

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StockApiTests.ReadJsonAsync<ProblemDetails>(retry))
            .Detail.Should().Be("That order already has a reservation for this product.");

        (await GetStockAsync(productId)).Reserved.Should().Be(2, "the retry must not double-hold");
    }

    [Fact]
    public async Task Reserve_ForDifferentOrdersAgainstTheSameProduct_BothHold()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);

        (await ReserveAsync(productId, Guid.NewGuid(), 3)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReserveAsync(productId, Guid.NewGuid(), 4)).StatusCode.Should().Be(HttpStatusCode.Created);

        var stock = await GetStockAsync(productId);

        stock.Reserved.Should().Be(7);
        stock.Available.Should().Be(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Reserve_WithANonPositiveQuantity_Returns400ValidationProblem(int quantity)
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);

        var response = await ReserveAsync(productId, Guid.NewGuid(), quantity);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StockApiTests.ReadJsonAsync<HttpValidationProblemDetails>(response))
            .Errors.Should().ContainKey("Quantity");
    }

    [Fact]
    public async Task Reserve_WithAnEmptyOrderId_Returns400()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);

        var response = await ReserveAsync(productId, Guid.Empty, 1);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_ShipsTheGoodsSoBothCountersDrop()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);
        var reservation = await CreatedReservationAsync(productId, 4);

        var response = await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/confirm", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StockApiTests.ReadJsonAsync<ReservationResponse>(response)).Status.Should().Be("Confirmed");

        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(6);
        stock.Reserved.Should().Be(0);
        stock.Available.Should().Be(6, "availability already fell when the hold was taken");
    }

    [Fact]
    public async Task Release_LiftsTheHoldWithoutShippingAnything()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);
        var reservation = await CreatedReservationAsync(productId, 4);

        var response = await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/release", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StockApiTests.ReadJsonAsync<ReservationResponse>(response)).Status.Should().Be("Released");

        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(10, "nothing was shipped");
        stock.Reserved.Should().Be(0);
        stock.Available.Should().Be(10, "the stock is sellable again");
    }

    [Fact]
    public async Task Confirm_Twice_Returns409AndDoesNotShipTwice()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);
        var reservation = await CreatedReservationAsync(productId, 4);

        await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/confirm", content: null);

        var retry = await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/confirm", content: null);

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StockApiTests.ReadJsonAsync<ProblemDetails>(retry))
            .Detail.Should().Be("The reservation is already confirmed and cannot be confirmed.");

        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(6, "a rejected retry must not ship the goods a second time");
        stock.Reserved.Should().Be(0);
    }

    [Fact]
    public async Task Release_AfterConfirm_Returns409()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);
        var reservation = await CreatedReservationAsync(productId, 4);

        await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/confirm", content: null);

        var response = await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/release", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StockApiTests.ReadJsonAsync<ProblemDetails>(response))
            .Detail.Should().Be("The reservation is already confirmed and cannot be released.");

        (await GetStockAsync(productId)).QuantityOnHand.Should().Be(6);
    }

    [Fact]
    public async Task Confirm_AfterRelease_Returns409()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);
        var reservation = await CreatedReservationAsync(productId, 4);

        await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/release", content: null);

        var response = await Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/confirm", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StockApiTests.ReadJsonAsync<ProblemDetails>(response))
            .Detail.Should().Be("The reservation is already released and cannot be confirmed.");

        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(10, "released stock was never shipped");
        stock.Reserved.Should().Be(0);
    }

    [Fact]
    public async Task Confirm_ForAnUnknownReservation_Returns404()
    {
        var response = await Client.PostAsync($"{ReservationsUrl}/{Guid.NewGuid()}/confirm", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Release_ForAnUnknownReservation_Returns404()
    {
        var response = await Client.PostAsync($"{ReservationsUrl}/{Guid.NewGuid()}/release", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListByOrder_ReturnsEveryReservationWithItsProduct()
    {
        var orderId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var otherProduct = Guid.NewGuid();

        await SeedStockAsync(first, 10);
        await SeedStockAsync(second, 10);
        await SeedStockAsync(otherProduct, 10);

        await CreatedReservationAsync(first, 2, orderId);
        await CreatedReservationAsync(second, 3, orderId);

        // A reservation belonging to a different order, which must not appear.
        await CreatedReservationAsync(otherProduct, 1, Guid.NewGuid());

        var response = await Client.GetAsync($"{ReservationsUrl}?orderId={orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var reservations = await StockApiTests.ReadJsonAsync<List<ReservationResponse>>(response);

        reservations.Should().HaveCount(2);
        reservations.Select(r => r.ProductId).Should().BeEquivalentTo([first, second]);
        reservations.Should().OnlyContain(r => r.OrderId == orderId);
        reservations.Should().OnlyContain(r => r.Status == "Pending");
    }

    [Fact]
    public async Task ListByOrder_SettledReservationsStillAppear()
    {
        // The compensation and reconciliation path needs history, not just live holds.
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);

        var confirmed = await CreatedReservationAsync(productId, 2, orderId);
        await Client.PostAsync($"{ReservationsUrl}/{confirmed.Id}/confirm", content: null);

        var reservations = await StockApiTests.ReadJsonAsync<List<ReservationResponse>>(
            await Client.GetAsync($"{ReservationsUrl}?orderId={orderId}"));

        reservations.Should().ContainSingle().Which.Status.Should().Be("Confirmed");
    }

    [Fact]
    public async Task ListByOrder_ForAnUnknownOrder_ReturnsAnEmptyList()
    {
        var response = await Client.GetAsync($"{ReservationsUrl}?orderId={Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StockApiTests.ReadJsonAsync<List<ReservationResponse>>(response)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListByOrder_WithoutAnOrderId_Returns400ProblemJson()
    {
        // The parameter is required, so a missing one is a binding failure — which must be a 400
        // from the handler, not a 500 and not an unbounded scan of every reservation.
        var response = await Client.GetAsync(ReservationsUrl);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task AFullLifecycleNeverBreaksTheInvariant()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, 10);

        await AssertInvariantAsync(productId, 10, 0, "after provisioning");

        var first = await CreatedReservationAsync(productId, 6);
        await AssertInvariantAsync(productId, 10, 6, "after holding 6");

        var second = await CreatedReservationAsync(productId, 4);
        await AssertInvariantAsync(productId, 10, 10, "after holding the remainder");

        await Client.PostAsync($"{ReservationsUrl}/{first.Id}/confirm", content: null);
        await AssertInvariantAsync(productId, 4, 4, "after shipping 6");

        await Client.PostAsync($"{ReservationsUrl}/{second.Id}/release", content: null);
        await AssertInvariantAsync(productId, 4, 0, "after releasing 4");
    }

    private async Task AssertInvariantAsync(Guid productId, int expectedOnHand, int expectedReserved, string because)
    {
        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(expectedOnHand, because);
        stock.Reserved.Should().Be(expectedReserved, because);
        stock.Reserved.Should().BeLessThanOrEqualTo(stock.QuantityOnHand, because);
        stock.Available.Should().BeGreaterThanOrEqualTo(0, because);
    }

    private async Task<StockResponse> GetStockAsync(Guid productId)
        => await StockApiTests.ReadJsonAsync<StockResponse>(await Client.GetAsync($"{StockUrl}/{productId}"));

    private async Task SeedStockAsync(Guid productId, int quantityOnHand)
    {
        var response = await Client.PostAsJsonAsync(StockUrl, new SetStockRequest(productId, quantityOnHand));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the stock record should have been created");
    }

    private Task<HttpResponseMessage> ReserveAsync(Guid productId, Guid orderId, int quantity)
        => Client.PostAsJsonAsync($"{StockUrl}/{productId}/reservations", new ReserveStockRequest(orderId, quantity));

    private async Task<ReservationResponse> CreatedReservationAsync(
        Guid productId,
        int quantity,
        Guid? orderId = null)
    {
        var response = await ReserveAsync(productId, orderId ?? Guid.NewGuid(), quantity);

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the reservation should have been created");

        return await StockApiTests.ReadJsonAsync<ReservationResponse>(response);
    }
}
