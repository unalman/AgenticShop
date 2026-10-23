using System.Net;
using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using FluentAssertions;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Everything that can go wrong during a placement: an unorderable product, a mixed-currency order,
/// and a Catalog that disappears while lines are being resolved.
/// </summary>
/// <remarks>
/// All of them happen while resolving lines against Catalog, before the idempotency key is claimed,
/// so every one writes nothing — no order row and no queued event. Stock cannot fail a placement any
/// more, because a placement does not call it: a refusal arrives later as an outcome and is
/// <see cref="StockOutcomeConsumerTests"/>' subject.
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

        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
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
        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
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

        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
        (await ReadAllOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolutionStopsAtTheFirstUnorderableProduct()
    {
        var known = AddProduct(name: "Anchor");
        var unknown = Guid.NewGuid();

        await PlaceAsync(OrderWith((known, 1), (unknown, 1)));

        Catalog.Requested.Should().Equal(known, unknown);
        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty(
            "nothing is queued until every line has been resolved");
    }

    // Five tests lived here that drove Stock failures through a placement: a refused line releasing
    // the earlier holds, an unreachable Stock answering 502, an unreadable Stock, a reserve that
    // applied and lost its response. All five went with the synchronous path, because none of those
    // things can happen during a placement any more — Stock is not called, so it cannot refuse, fault
    // or time out inside the request. A refusal is now an asynchronous outcome that settles the order
    // as Failed, which is StockOutcomeConsumerTests' subject, and it is asserted there against a real
    // broker rather than against a fake client.
    //
    // What is left in this file is the only failure a placement can still produce: Catalog's. That
    // happens while resolving lines, before the idempotency key is claimed, so it writes nothing.

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

        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
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
