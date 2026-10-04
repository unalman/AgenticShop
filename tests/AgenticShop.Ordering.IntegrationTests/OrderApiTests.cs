using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using AgenticShop.Shared.Health;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// The path that has to work before any of the failure paths are worth testing: an order goes in,
/// stock is held and settled, and what comes back is a snapshot rather than a set of references.
/// </summary>
[Collection("Ordering API")]
public sealed class OrderApiTests(OrderingApiFixture fixture) : OrderApiTestBase(fixture)
{
    private const string CorrelationIdHeader = CorrelationIdMiddleware.HeaderName;

    /// <summary>
    /// A W3C trace id — the only form the correlation id takes now that an inbound value is ignored
    /// rather than adopted.
    /// </summary>
    private const string TraceIdPattern = "^[0-9a-f]{32}$";

    [Fact]
    public async Task PlacingAnOrder_ReservesConfirmsAndReturns201WithALocation()
    {
        var product = AddProduct(name: "Blue Widget", price: 12.50m);

        var response = await PlaceAsync(OrderWith((product, 2)));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().StartWith("/api/v1/orders/");

        var body = await BodyAsync(response);

        body.Status.Should().Be(nameof(OrderStatus.Confirmed));
        body.OrderNumber.Should().MatchRegex(@"^ORD-\d{8}-[0-9a-f]{8}$");
        body.Currency.Should().Be("USD");
        body.TotalAmount.Should().Be(25.00m);
        body.Lines.Should().ContainSingle();
        body.Lines[0].Quantity.Should().Be(2);
        body.Lines[0].UnitPrice.Should().Be(12.50m);
        body.Lines[0].LineTotal.Should().Be(25.00m);
        body.Lines[0].ProductName.Should().Be("Blue Widget");

        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(1);
        Stock.CountCalls(FakeStockClient.ConfirmOperation).Should().Be(1);
        Stock.CountCalls(FakeStockClient.ReleaseOperation).Should().Be(0, "nothing needed compensating");
    }

    [Fact]
    public async Task TheOrderIsWrittenOnceWithATerminalStatusAndAllItsLines()
    {
        var first = AddProduct(name: "Anchor", price: 4.00m);
        var second = AddProduct(name: "Rope", price: 7.25m);

        var body = await PlaceSuccessfullyAsync((first, 1), (second, 3));

        var orders = await ReadAllOrdersAsync();

        orders.Should().ContainSingle("one placement writes one order");

        var persisted = orders.Single();

        persisted.Id.Should().Be(body.Id);
        persisted.Status.Should().Be(OrderStatus.Confirmed);
        persisted.IsTerminal.Should().BeTrue();
        persisted.Lines.Should().HaveCount(2);
        persisted.TotalAmount.Should().Be(4.00m + 21.75m);
        persisted.Lines.Sum(line => line.LineTotal).Should().Be(persisted.TotalAmount);
    }

    [Fact]
    public async Task NoOrderIsEverPersistedAsPending()
    {
        // The constraint the confirm-phase policy depends on: the row is written once, after
        // reservation, confirmation and reconciliation, and already terminal. A Pending row in Phase 0
        // would be a row nothing can advance, because there is no worker.
        var product = AddProduct();

        await PlaceSuccessfullyAsync((product, 1));

        (await ReadAllOrdersAsync()).Should().OnlyContain(order => order.Status != OrderStatus.Pending);
    }

    [Fact]
    public async Task ALineSnapshotsTheCatalogSoALaterEditCannotRewriteOrderHistory()
    {
        var product = AddProduct(name: "Blue Widget", price: 12.50m);

        var body = await PlaceSuccessfullyAsync((product, 1));

        // The product changes after the order exists. Catalog owns the product; the order owns its
        // snapshot, and the two are now allowed to disagree.
        Catalog.WithProduct(product, name: "Renamed And Repriced", price: 99.99m);

        var reread = await ReadOrderAsync(body.Id);

        reread!.Lines.Should().ContainSingle();
        reread.Lines.Single().ProductName.Should().Be("Blue Widget");
        reread.Lines.Single().UnitPrice.Should().Be(12.50m);
        reread.TotalAmount.Should().Be(12.50m);
    }

    [Fact]
    public async Task DuplicateProductLinesAreCombinedIntoOneHold()
    {
        // Stock's UNIQUE(order_id, stock_item_id) allows one hold per product per order, so the
        // second line would simply be refused. Combining is lossless: the unit price comes from
        // Catalog and is the same for both.
        var product = AddProduct(price: 5.00m);

        var body = await PlaceSuccessfullyAsync((product, 2), (product, 3));

        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(1);
        Stock.Reservations.Should().ContainSingle();
        Stock.Reservations[0].Quantity.Should().Be(5);

        body.Lines.Should().ContainSingle();
        body.Lines[0].Quantity.Should().Be(5);
        body.TotalAmount.Should().Be(25.00m);
    }

    [Fact]
    public async Task EveryLineIsResolvedAgainstCatalogBeforeAnythingIsReserved()
    {
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        await PlaceSuccessfullyAsync((first, 1), (second, 1));

        // Resolution happens up front, so a rejected product costs no compensation.
        Catalog.Requested.Should().Equal(first, second);
    }

    [Fact]
    public async Task AnInboundCorrelationIdIsIgnoredAndTheTraceIdReturned()
    {
        // X-Correlation-Id is response-only now: the id is the ambient W3C trace id, so an inbound
        // value has nothing to be adopted into. The header it used to travel on is still answered,
        // which is what lets a caller join a response to the log lines and the span that explain it.
        var product = AddProduct();

        var response = await PlaceAsync(OrderWith((product, 1)), correlationId: "trace-1");

        response.Headers.GetValues(CorrelationIdHeader)
            .Should().ContainSingle()
            .Which.Should().MatchRegex(TraceIdPattern)
            .And.NotBe("trace-1");
    }

    [Fact]
    public async Task TheCorrelationIdIsATraceId()
    {
        var product = AddProduct();

        var response = await PlaceAsync(OrderWith((product, 1)));

        response.Headers.GetValues(CorrelationIdHeader)
            .Should().ContainSingle()
            .Which.Should().MatchRegex(TraceIdPattern, "a W3C trace id, not a GUID");
    }

    [Fact]
    public async Task AProblemDetailsBodyCarriesTheSameCorrelationIdAsTheHeader()
    {
        // The id is what joins a response to the log line that explains it, so it has to survive the
        // failure path too — and to survive it unchanged, not reminted. Asserted header-against-body
        // rather than against a value the caller sent, because the caller no longer chooses it.
        var response = await PlaceAsync(OrderWith((Guid.NewGuid(), 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var correlationId = response.Headers.GetValues(CorrelationIdHeader).Single();
        correlationId.Should().MatchRegex(TraceIdPattern);

        var problem = await ProblemAsync(response);

        problem.Extensions["correlationId"]!.ToString().Should().Be(correlationId);
    }

    [Fact]
    public async Task AValidationProblemCarriesTheCorrelationIdInTheHeaderOnly()
    {
        // Pinned rather than fixed. DataAnnotationValidationFilter builds its own
        // ValidationProblemDetails and does not add the extension the exception handler adds, so the
        // body carries no correlationId. The header always does, which is enough to join a response to
        // a log line — and since Serilog request logging landed, a validation rejection does produce
        // one, so there is a line to join it to.
        //
        // Closing the body gap would mean changing the shared filter, which is deliberately left
        // alone: it is specified once in AgenticShop.Shared and the header already carries the id.
        var response = await PlaceRawAsync("{\"lines\": []}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.GetValues(CorrelationIdHeader)
            .Should().ContainSingle()
            .Which.Should().MatchRegex(TraceIdPattern);

        var problem = await ProblemAsync(response);

        problem.Extensions.Should().NotContainKey("correlationId");
    }

    [Fact]
    public async Task GetOrder_ReturnsTheOrderWithItsLines()
    {
        var product = AddProduct(name: "Blue Widget", price: 3.00m);
        var placed = await PlaceSuccessfullyAsync((product, 4));

        var response = await Client.GetAsync($"/api/v1/orders/{placed.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);

        body.Id.Should().Be(placed.Id);
        body.OrderNumber.Should().Be(placed.OrderNumber);
        body.Status.Should().Be(nameof(OrderStatus.Confirmed));
        body.TotalAmount.Should().Be(12.00m);
        body.Lines.Should().ContainSingle();
    }

    [Fact]
    public async Task GetOrder_Returns404ForAnUnknownId()
    {
        var response = await Client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnOrderWithNoLines_IsAValidationProblem()
    {
        var response = await PlaceAsync(new CreateOrderRequest([]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadValidationProblemAsync(response);

        problem.Errors.Should().ContainKey("Lines");
        Catalog.Requested.Should().BeEmpty("rejected at the boundary, before any outbound call");
        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(0);
    }

    [Fact]
    public async Task ABadQuantityIsReportedAgainstTheNestedMember()
    {
        // The first genuinely nested request DTO in the repository. Until now the filter's recursion
        // was proven only by synthetic contracts, so a collection of lines is the case that would
        // have silently gone unvalidated: reflecting over a List<T> yields Count and Capacity, not
        // its contents.
        var product = AddProduct();

        var response = await PlaceAsync(OrderWith((product, 0)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadValidationProblemAsync(response);

        problem.Errors.Should().ContainKey("Lines[0].Quantity");
        problem.Errors["Lines[0].Quantity"].Should().NotBeEmpty();

        Stock.CountCalls(FakeStockClient.ReserveOperation).Should().Be(0);
    }

    [Fact]
    public async Task EveryBadLineIsReportedNotJustTheFirst()
    {
        var product = AddProduct();

        var response = await PlaceAsync(OrderWith((product, 0), (product, -1)));

        var problem = await ReadValidationProblemAsync(response);

        problem.Errors.Keys.Should().Contain(["Lines[0].Quantity", "Lines[1].Quantity"]);
    }

    [Fact]
    public async Task MoreLinesThanTheMaximum_IsAValidationProblem()
    {
        var product = AddProduct();

        var response = await PlaceAsync(
            new CreateOrderRequest(
                [.. Enumerable.Range(0, Order.MaxLines + 1)
                    .Select(_ => new CreateOrderLineRequest(product, 1))]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadValidationProblemAsync(response);

        problem.Errors.Should().ContainKey("Lines");
        Catalog.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedJson_IsA400NotA500()
    {
        // A binding failure, so it never reaches the validation filter — the handler's
        // BadHttpRequestException arm is what answers it, with the framework's own status code.
        var response = await PlaceRawAsync("{\"lines\": [");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ProblemAsync(response);

        problem.Title.Should().Be("Bad request.");
        problem.Detail.Should().BeNull("a binding failure's message describes our own binding");
    }

    [Fact]
    public async Task Health_StaysHealthyWhenBothDownstreamsAreUnreachable()
    {
        // The property the whole two-endpoint split exists for, and this host is an unusually direct
        // way to test it: the factory points both downstreams at *.invalid, so neither can be reached.
        // Liveness must not care. If /health reported Unhealthy here, an orchestrator would pull
        // Ordering out of rotation because Stock was down — and Ordering can still answer every read
        // it has, and would honestly refuse placements with a 502.
        var response = await Client.GetAsync(HealthEndpoint.Path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await ReadHealthEntriesAsync(response);

        entries.Should().ContainKey(HealthEndpoint.DatabaseCheckName);
        entries[HealthEndpoint.DatabaseCheckName].Should().Be("Healthy");

        entries.Should().NotContainKey("Catalog", "a downstream check is readiness-only");
        entries.Should().NotContainKey("Stock");
    }

    [Fact]
    public async Task HealthReady_ReportsEachUnreachableDownstreamByName()
    {
        // Readiness is the endpoint that is allowed to depend on other services, and it has to say
        // *which* one — "Unhealthy" alone is what the framework's default writer gives and is the
        // reason for the custom one.
        var response = await Client.GetAsync(HealthEndpoint.ReadyPath);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var entries = await ReadHealthEntriesAsync(response);

        entries.Should().ContainKey(HealthEndpoint.DatabaseCheckName);
        entries[HealthEndpoint.DatabaseCheckName].Should().Be("Healthy", "the database is up");
        entries["Catalog"].Should().Be("Unhealthy");
        entries["Stock"].Should().Be("Unhealthy");
    }

    [Fact]
    public async Task HealthReady_DoesNotLeakTheDownstreamAddress()
    {
        // The probe knows a base URL that came from configuration. Whatever it reports, the response
        // must not become a way to read this service's configuration — the same rule the exception
        // handler follows, and the reason descriptions are Development-only.
        var body = await (await Client.GetAsync(HealthEndpoint.ReadyPath))
            .Content.ReadAsStringAsync();

        body.Should().NotContain("catalog.invalid");
        body.Should().NotContain("stock.invalid");
        body.Should().NotContain("Exception");
    }

    private static async Task<HttpValidationProblemDetails> ReadValidationProblemAsync(
        HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        problem!.Errors.Should().NotBeEmpty();

        return problem;
    }

    /// <summary>
    /// The response shape is specified once, in <c>AgenticShop.Shared.UnitTests</c>; this reads only
    /// what a wiring assertion needs, entry name to status. Duplicated in each service's integration
    /// assembly on purpose — the three must not reference each other.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadHealthEntriesAsync(
        HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return json.RootElement.GetProperty("entries")
            .EnumerateObject()
            .ToDictionary(
                entry => entry.Name,
                entry => entry.Value.GetProperty("status").GetString()!);
    }
}
