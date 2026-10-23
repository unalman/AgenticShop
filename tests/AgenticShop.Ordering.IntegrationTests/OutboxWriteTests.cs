using System.Net;
using System.Text.Json;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using AgenticShop.Ordering.Messaging;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// What the outbox row itself has to get right — asserted with no broker anywhere near it.
/// </summary>
/// <remarks>
/// <para>
/// These run in the API collection, whose factory points the publisher at an unresolvable host. That
/// is what makes the assertions stable rather than merely convenient: the row is written inside the
/// placement's own transaction, so it exists the moment the response does, and nothing can publish it
/// out from under the test. Whether a row is ever <i>delivered</i> is
/// <see cref="OutboxPublisherTests"/>'s question and needs a real broker.
/// </para>
/// <para>
/// The split is deliberate. Asserting "the row is still unpublished" against a reachable broker would
/// be a race with a background loop, and a test that flakes is worse than no test — it teaches the
/// next reader to retry rather than to look.
/// </para>
/// </remarks>
[Collection("Ordering API")]
public sealed class OutboxWriteTests(OrderingApiFixture fixture) : OrderApiTestBase(fixture)
{
    [Fact]
    public async Task AConfirmedPlacementWritesOneOrderPlacedRowInTheSameTransaction()
    {
        var product = AddProduct(name: "Blue Widget", price: 12.50m);

        var body = await PlaceSuccessfullyAsync((product, 2));

        var rows = await ReadAllOutboxMessagesAsync();

        rows.Should().ContainSingle("one placement writes one event");

        var row = rows.Single();

        row.Exchange.Should().Be(OrderEventTopology.Exchange);
        row.RoutingKey.Should().Be(OrderEventTopology.OrderPlacedRoutingKey);
        row.IsPublished.Should().BeFalse(
            "this fixture's broker cannot be reached, so the row is committed and still waiting");
        row.PublishedAtUtc.Should().BeNull();

        // The payload is captured at write time rather than rendered at publish time, so it must
        // already agree with the response the caller was given.
        var payload = JsonSerializer.Deserialize<OrderPlaced>(row.PayloadJson, OrderPlaced.JsonOptions);

        payload.Should().NotBeNull("the row stores a serialised OrderPlaced");
        payload!.MessageId.Should().Be(row.Id,
            "the row id is the message id, and the payload carries the same value so the two cannot disagree");
        payload.OrderId.Should().Be(body.Id);
        payload.OrderNumber.Should().Be(body.OrderNumber);
        payload.Currency.Should().Be(body.Currency);
        payload.OccurredAtUtc.Should().Be(body.PlacedAtUtc);

        payload.Lines.Should().ContainSingle();
        payload.Lines[0].ProductId.Should().Be(product);
        payload.Lines[0].Quantity.Should().Be(2);

        // Stock holds no money at all, so no price crosses the wire. Asserted on the serialised
        // payload rather than on the record's shape, because the payload is the contract a consumer
        // actually reads — and a price here would be a second truth for a number Catalog owns.
        row.PayloadJson.Should().NotContain("price");
        row.PayloadJson.Should().NotContain("Price");
    }

    [Fact]
    public async Task TheRowCarriesTheTraceIdTheCallerWasGiven()
    {
        var product = AddProduct();

        var response = await PlaceAsync(OrderWith((product, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var correlationId = response.Headers
            .GetValues(CorrelationIdMiddleware.HeaderName)
            .Single();

        var row = (await ReadAllOutboxMessagesAsync()).Single();

        // The publisher runs on a background loop with no request and therefore no ambient Activity,
        // so this column is the only thing that can carry the trace across the broker hop. Without it
        // the placement's spans and the consumer's would be two unrelated trees, and the gap would be
        // visible only in a trace nobody thought to open.
        row.TraceParent.Should().NotBeNull("a placement always runs inside a request activity");
        row.TraceParent!.Should().HaveLength(OutboxMessage.MaxTraceParentLength,
            "a W3C traceparent is exactly 55 characters");
        row.TraceParent.Should().Contain(correlationId,
            "the correlation id is the trace id, so the message must be traceable back to the request that wrote it");
    }

    [Fact]
    public async Task APlacementThatStockRefusesWritesNoOutboxRow()
    {
        var product = AddProduct();

        Stock.ReserveFailsWith = FakeStockClient.Failure.Refused;

        var response = await PlaceAsync(OrderWith((product, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty(
            "an order that was never placed has nothing for a consumer to act on, and a rejection is " +
            "not this event — it is the saga's to publish once reservation moves behind the message");
    }

    private async Task<List<OutboxMessage>> ReadAllOutboxMessagesAsync()
    {
        using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .ToListAsync();
    }
}
