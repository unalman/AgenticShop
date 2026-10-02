using System.Net;
using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using FluentAssertions;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Everything that can go wrong before a single hold is settled: an unorderable product, a
/// mixed-currency order, a refused reserve, and a downstream that disappears mid-reservation.
/// </summary>
/// <remarks>
/// These are the cases decision B covers — fail fast, release what was held, fail the order. The
/// confirm-phase cases, which cannot be undone so cleanly, are in
/// <see cref="ConfirmPhaseFailureTests"/>.
/// </remarks>
[Collection("Ordering API")]
public sealed class PlacementFailureTests(OrderingApiFixture fixture) : OrderApiTestBase(fixture)
{
    [Fact]
    public async Task AProductCatalogDoesNotHave_Returns409AndReservesNothing()
    {
        var unknown = Guid.NewGuid();

        var response = await PlaceAsync(OrderWith((unknown, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await ProblemAsync(response);

        problem.Detail.Should().Be($"Product {unknown} cannot be ordered.");
        problem.Extensions.Should().NotContainKey("orderId", "nothing was reserved, so no order was written");

        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(0);
        (await ReadAllOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnInactiveProduct_Returns409AndReservesNothing()
    {
        // Catalog's query filter already answers 404 for a soft-deleted product, so this branch is
        // defence in depth: it is reachable only if that filter is removed or an includeInactive path
        // is added. An order must never be placed against a product nobody can buy.
        var deactivated = AddProduct(isActive: false);

        var response = await PlaceAsync(OrderWith((deactivated, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(0);
        (await ReadAllOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AMixedCurrencyOrder_Returns400AndReservesNothing()
    {
        // A total summed across currencies is a meaningless number, and it would be written into
        // order history. Caught during resolution, so it costs no compensation.
        var usd = AddProduct(name: "Anchor", price: 10.00m, currency: "USD");
        var eur = AddProduct(name: "Rope", price: 10.00m, currency: "EUR");

        var response = await PlaceAsync(OrderWith((usd, 1), (eur, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ProblemAsync(response);

        // Detail is null, like every other domain 400 in the repository: no exception message
        // reaches the client. Which currencies clashed is in the log line, joined to this response
        // by the correlation id.
        problem.Detail.Should().BeNull();
        problem.Extensions["correlationId"].Should().NotBeNull();

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("USD").And.NotContain("EUR");

        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(0);
        (await ReadAllOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolutionStopsAtTheFirstUnorderableProduct()
    {
        var known = AddProduct(name: "Anchor");
        var unknown = Guid.NewGuid();

        await PlaceAsync(OrderWith((known, 1), (unknown, 1)));

        Catalog.Requested.Should().Equal(known, unknown);
        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(0,
            "nothing is reserved until every line has been resolved");
    }

    [Fact]
    public async Task WhenStockRefusesALine_TheEarlierHoldsAreReleasedAndTheOrderIsFailed()
    {
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");
        var third = AddProduct(name: "Chain");

        // The middle line is refused. Under contention this is the ordinary case, not an edge: Stock
        // measured a 40-way burst holding only 4 of 10 units, because it does not retry on an xmin
        // conflict.
        Stock.ReserveFailsWith = FakeStockClient.Failure.Refused;
        Stock.ReserveFailsFromCall = 1;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await ProblemAsync(response);
        problem.Detail.Should().Be($"Not enough stock is available for product {second}.");

        var orderId = OrderIdFrom(problem);

        // Fail fast: the third line is never attempted, and the one hold that was taken is given back.
        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(2);
        Stock.CountCalls(FakeStockClient.ConfirmOperation).Should().Be(0, "nothing was settled");
        Stock.CountCalls(FakeStockClient.ReleaseOperation).Should().Be(1);
        Stock.Reservations.Should().OnlyContain(r => r.Status == StockReservationSnapshot.Statuses.Released);

        var persisted = await ReadOrderAsync(orderId);

        persisted!.Status.Should().Be(OrderStatus.Failed);
        persisted.Lines.Should().HaveCount(3, "the order records what was asked for, not what was held");
    }

    [Fact]
    public async Task AFailedOrderIsStillQueryable()
    {
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        Stock.ReserveFailsWith = FakeStockClient.Failure.Refused;
        Stock.ReserveFailsFromCall = 1;

        var problem = await ProblemAsync(await PlaceAsync(OrderWith((first, 1), (second, 1))));

        var response = await Client.GetAsync($"/api/v1/orders/{OrderIdFrom(problem)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);

        body.Status.Should().Be(nameof(OrderStatus.Failed));
        body.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task WhenStockIsUnreachableWhileReserving_TheOrderIsFailedNotPartiallyConfirmed()
    {
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        Stock.ReserveFailsWith = FakeStockClient.Failure.Fault;
        Stock.ReserveFailsFromCall = 1;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var persisted = await ReadOrderAsync(OrderIdFrom(await ProblemAsync(response)));

        // Nothing can have shipped — no confirm was ever issued — so Failed stays truthful and the
        // fourth status is not needed. This is the asymmetry the policy turns on.
        persisted!.Status.Should().Be(OrderStatus.Failed);
    }

    [Fact]
    public async Task WhenTheReserveReadBackAlsoFails_TheOrderIsStillFailed()
    {
        // The reserve phase is the one place where an unreadable Stock does not force
        // PartiallyConfirmed, because "we do not know" can only mean "a hold may be stranded", and a
        // stranded hold ships nothing.
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        Stock.ReserveFailsWith = FakeStockClient.Failure.Fault;
        Stock.ReserveFailsFromCall = 1;
        Stock.ListFails = true;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var persisted = await ReadOrderAsync(OrderIdFrom(await ProblemAsync(response)));

        persisted!.Status.Should().Be(OrderStatus.Failed);
    }

    [Fact]
    public async Task AReserveThatAppliedButLostItsResponse_IsFoundByTheReadAndReleased()
    {
        // The reservation really exists in Stock and its id was never returned, so the only way to
        // give the hold back is to read the order's reservations and release what is still pending.
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        Stock.ReserveFailsWith = FakeStockClient.Failure.AppliedThenLost;
        Stock.ReserveFailsFromCall = 1;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        Stock.CountCalls(FakeStockClient.ListOperation).Should().Be(1);
        Stock.Reservations.Should().HaveCount(2);
        Stock.Reservations.Should().OnlyContain(r => r.Status == StockReservationSnapshot.Statuses.Released);
    }

    [Fact]
    public async Task ACatalogOutage_Returns502AndWritesNoOrder()
    {
        var product = AddProduct();

        Catalog.FailsWith = FakeCatalogClient.Failure.ServerError;

        var response = await PlaceAsync(OrderWith((product, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var problem = await ProblemAsync(response);

        problem.Title.Should().Be("Downstream failure.");
        problem.Detail.Should().NotContain("Catalog", "naming a dependency discloses internal topology");
        problem.Extensions.Should().NotContainKey("orderId", "nothing was held, so nothing was written");

        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(0);
        (await ReadAllOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ACatalogTimeoutIsAlsoA502AndNotACancellation()
    {
        // HttpClient reports its own timeout as TaskCanceledException, which the handler has an arm
        // for — guarded on RequestAborted. Without the client catching it, a Catalog timeout would
        // produce no response at all instead of a 502.
        var product = AddProduct();

        Catalog.FailsWith = FakeCatalogClient.Failure.Timeout;

        var response = await PlaceAsync(OrderWith((product, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadAllOrdersAsync()).Should().BeEmpty();
    }
}
