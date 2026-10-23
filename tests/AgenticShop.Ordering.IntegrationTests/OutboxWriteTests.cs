using System.Net;
using System.Text.Json;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.Messaging;
using AgenticShop.Shared.Middleware;
using FluentAssertions;

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
/// <para>
/// Every accepted placement now writes a row, unconditionally. That was not true when the outbox was
/// first added — reservation was synchronous and only a <c>Confirmed</c> order had anything for a
/// consumer to do — and the change is the flip: the row is now the <i>cause</i> of the reservation
/// rather than a record of one that already happened.
/// </para>
/// </remarks>
[Collection("Ordering API")]
public sealed class OutboxWriteTests(OrderingApiFixture fixture) : OrderApiTestBase(fixture)
{
    [Fact]
    public async Task AnAcceptedPlacementWritesOneOrderPlacedRowInTheSameTransaction()
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

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

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

    // APlacementThatStockRefusesWritesNoOutboxRow used to live here. The scenario it covered no
    // longer exists: Stock is not called during a placement, so it cannot refuse one, and every
    // accepted placement queues an event. A refusal is now an inbound outcome that settles the order
    // as Failed, which is StockOutcomeConsumerTests' subject.
    //
    // The property it was really guarding — that a row is only written for an order that was actually
    // accepted — is still covered, from the other side: PlacementFailureTests asserts that a Catalog
    // failure writes no order at all, and ARepeatedKeyReplaysTheOrderAndPlacesNothingAgain asserts
    // that a replayed key queues exactly one event. Between them there is no path that writes a row
    // without an accepted order behind it.
}
