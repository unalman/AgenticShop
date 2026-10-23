using System.Text.Json;
using AgenticShop.Stock.Domain;
using AgenticShop.Stock.Messaging;
using FluentAssertions;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// That the consumer holds stock, answers exactly once per message, and never half-commits an order.
/// </summary>
/// <remarks>
/// <para>
/// These run against a real broker with the two hosted services registered by the fixture rather than
/// by <c>Program.cs</c>, so the code under test is the code that will ship. Nothing here fakes the
/// consumer: a message goes onto a queue, is delivered over AMQP, and the assertions read PostgreSQL
/// and the outcome queue afterwards.
/// </para>
/// <para>
/// <b>Completion is observed, not slept on.</b> Every test waits on a row that the consumer's own
/// transaction writes, so a green run means the work happened rather than that enough time passed.
/// The one place that needs to prove something did <i>not</i> happen uses ordering instead of a
/// timeout — see <see cref="ARejectedMessageStaysRejectedAfterStockArrives"/>.
/// </para>
/// </remarks>
[Collection("Stock Messaging")]
public sealed class OrderPlacedConsumerTests(StockMessagingFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A W3C traceparent, well-formed and recognisable, used to prove it survives two hops.</summary>
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AnOrderPlacedTakesTheStockAndAnswersWithStockCommitted()
    {
        var productId = Guid.NewGuid();
        await fixture.SeedStockAsync(productId, quantityOnHand: 10);

        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await fixture.PublishOrderPlacedAsync(
            messageId,
            orderId,
            "ORD-TEST-0001",
            [new OrderPlacedLine(productId, 2)],
            TraceParent);

        // The inbox row and the reservation commit together, so either one is proof of both.
        await WaitUntilAsync(
            async () => (await fixture.ReadInboxAsync()).Count == 1,
            "the consumer should have processed the delivery");

        var reservations = await fixture.ReadReservationsAsync();

        reservations.Should().ContainSingle();
        reservations[0].OrderId.Should().Be(orderId);
        reservations[0].Quantity.Should().Be(2);
        reservations[0].Status.Should().Be(ReservationStatus.Confirmed,
            "the consumer reserves and confirms in one transaction, so Pending is never persisted from " +
            "this path — the same shape Order.Pending had in Phase 0");

        var item = await fixture.ReadItemAsync(productId);

        item.Should().NotBeNull();
        item!.QuantityOnHand.Should().Be(8, "two units were shipped, not merely held");
        item.Reserved.Should().Be(0, "reserve added two and confirm took them back off in the same transaction");
        item.Available.Should().Be(8);

        // And the outcome reaches a consumer of Stock's exchange, carrying the producer's trace.
        var outcome = await fixture.ConsumeOutcomeAsync();

        outcome.RoutingKey.Should().Be(StockEventTopology.CommittedRoutingKey);

        var committed = JsonSerializer.Deserialize<StockCommitted>(outcome.Body, StockMessageJson.Options);

        committed.Should().NotBeNull();
        committed!.OrderId.Should().Be(orderId);
        committed.Lines.Should().ContainSingle();
        committed.Lines[0].ProductId.Should().Be(productId);
        committed.Lines[0].Quantity.Should().Be(2);

        outcome.MessageId.Should().Be(committed.MessageId.ToString(),
            "the AMQP property and the payload are set from one value");

        outcome.TraceParent.Should().Be(TraceParent,
            "the trace has to survive two broker hops, or the placement's spans and Stock's work are " +
            "two unrelated trees");

        var outbox = await fixture.ReadOutboxAsync();

        outbox.Should().ContainSingle();
        outbox[0].Id.Should().Be(committed.MessageId);
        outbox[0].RoutingKey.Should().Be(StockEventTopology.CommittedRoutingKey);

        await WaitUntilAsync(
            async () => (await fixture.ReadOutboxAsync()).Single().IsPublished,
            "the publisher should have drained the outcome row");
    }

    [Fact]
    public async Task ARedeliveredMessageDoesNotCommitTwice()
    {
        var productId = Guid.NewGuid();
        await fixture.SeedStockAsync(productId, quantityOnHand: 10);

        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lines = new[] { new OrderPlacedLine(productId, 3) };

        await fixture.PublishOrderPlacedAsync(messageId, orderId, "ORD-TEST-0002", lines);

        await WaitUntilAsync(
            async () => (await fixture.ReadReservationsAsync()).Count == 1,
            "the first delivery should have been held");

        // Same message id, same body: what a publisher whose row was lost after the broker
        // acknowledged it would send again.
        await fixture.PublishOrderPlacedAsync(messageId, orderId, "ORD-TEST-0002", lines);

        // Proof of processing without a timeout: publish a *second*, distinct message and wait on it.
        // With prefetch 1 and serial dispatch, if this one was handled then the redelivery before it
        // was handled too — so the assertions below are about a completed state, not a guessed delay.
        var laterMessageId = Guid.NewGuid();
        var laterOrderId = Guid.NewGuid();

        await fixture.PublishOrderPlacedAsync(
            laterMessageId, laterOrderId, "ORD-TEST-0003", [new OrderPlacedLine(productId, 1)]);

        await WaitUntilAsync(
            async () => (await fixture.ReadInboxAsync()).Count == 2,
            "the second, distinct message should have been processed, which means the redelivery was too");

        var reservations = await fixture.ReadReservationsAsync();

        reservations.Should().HaveCount(2, "one reservation per message, and the redelivery is not a message");
        reservations.Count(r => r.OrderId == orderId).Should().Be(1);
        reservations.Single(r => r.OrderId == orderId).Quantity.Should().Be(3);

        var item = await fixture.ReadItemAsync(productId);

        item!.QuantityOnHand.Should().Be(6,
            "3 for the first order plus 1 for the second — a redelivery that committed would make it 3");
        item.Reserved.Should().Be(0,
            "each line is confirmed in the transaction that reserves it, so nothing is left held");

        var inbox = await fixture.ReadInboxAsync();

        inbox.Should().ContainSingle(message => message.MessageId == messageId);
        inbox.Should().ContainSingle(message => message.MessageId == laterMessageId);
    }

    /// <summary>
    /// The case Stock's <c>UNIQUE(order_id, stock_item_id)</c> index cannot cover, and the reason the
    /// inbox exists rather than being belt-and-braces over that index.
    /// </summary>
    /// <remarks>
    /// A refused hold writes no reservation row, so a redelivery has nothing to collide with. If the
    /// consumer relied on the unique index alone, this sequence would answer one order with
    /// <c>StockReservationRejected</c> and then, once stock arrived, with <c>StockCommitted</c> — two
    /// contradictory outcomes for one message id, and a saga that cannot tell which to believe.
    /// </remarks>
    [Fact]
    public async Task ARejectedMessageStaysRejectedAfterStockArrives()
    {
        var productId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // No stock item exists yet, so this is refused — and writes no reservation to collide with.
        await fixture.PublishOrderPlacedAsync(
            messageId, orderId, "ORD-TEST-0004", [new OrderPlacedLine(productId, 4)]);

        await WaitUntilAsync(
            async () => (await fixture.ReadInboxAsync()).Count == 1,
            "the refusal should have been recorded");

        (await fixture.ReadReservationsAsync()).Should().BeEmpty("nothing was held");

        var rejection = await fixture.ConsumeOutcomeAsync();

        rejection.RoutingKey.Should().Be(StockEventTopology.ReservationRejectedRoutingKey);

        var rejected = JsonSerializer.Deserialize<StockReservationRejected>(
            rejection.Body, StockMessageJson.Options);

        rejected!.OrderId.Should().Be(orderId);
        rejected.Reason.Should().Be(RejectionReason.UnknownProduct);
        rejected.FailedProductId.Should().Be(productId);

        // Now the stock arrives, and the same message is redelivered.
        await fixture.SeedStockAsync(productId, quantityOnHand: 10);

        await fixture.PublishOrderPlacedAsync(
            messageId, orderId, "ORD-TEST-0004", [new OrderPlacedLine(productId, 4)]);

        // Ordered proof again rather than a timeout: a distinct message published afterwards.
        var laterMessageId = Guid.NewGuid();
        var laterProductId = Guid.NewGuid();

        await fixture.SeedStockAsync(laterProductId, quantityOnHand: 5);
        await fixture.PublishOrderPlacedAsync(
            laterMessageId, Guid.NewGuid(), "ORD-TEST-0005", [new OrderPlacedLine(laterProductId, 1)]);

        await WaitUntilAsync(
            async () => (await fixture.ReadInboxAsync()).Count == 2,
            "the later message should have been processed, so the redelivery before it was too");

        var reservations = await fixture.ReadReservationsAsync();

        // The later, distinct message is *meant* to hold one unit of its own product — it is the
        // ordering proof, not part of the assertion. What must not exist is a hold for the order that
        // was already refused.
        reservations.Should().ContainSingle("only the later message should have held anything");
        reservations.Should().NotContain(
            reservation => reservation.OrderId == orderId,
            "the redelivery must not have held anything: the order was already answered with a refusal, " +
            "and answering it again with a hold is the contradiction the inbox exists to prevent");

        // Asserted on quantity_on_hand rather than reserved, and that is not a stylistic choice: with
        // reserve-and-confirm in one transaction `reserved` ends at zero on every path, including the
        // one where the redelivery wrongly succeeded. An assertion that cannot distinguish success from
        // failure is not a guard.
        (await fixture.ReadItemAsync(productId))!.QuantityOnHand.Should().Be(10,
            "stock arrived after the refusal, and the redelivery must not have retroactively taken it");

        (await fixture.ReadInboxAsync()).Should().ContainSingle(m => m.MessageId == messageId);
    }

    [Fact]
    public async Task OneUnfulfillableLineRollsBackTheWholeOrder()
    {
        var plentiful = Guid.NewGuid();
        var scarce = Guid.NewGuid();

        await fixture.SeedStockAsync(plentiful, quantityOnHand: 10);
        await fixture.SeedStockAsync(scarce, quantityOnHand: 1);

        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await fixture.PublishOrderPlacedAsync(
            messageId,
            orderId,
            "ORD-TEST-0006",
            [new OrderPlacedLine(plentiful, 2), new OrderPlacedLine(scarce, 5)]);

        await WaitUntilAsync(
            async () => (await fixture.ReadInboxAsync()).Count == 1,
            "the refusal should have been recorded");

        // All-or-nothing: the first line was held in memory before the second failed, and that
        // mutation must not have reached the database.
        (await fixture.ReadReservationsAsync()).Should().BeEmpty(
            "a partial hold is worse than none — it strands inventory against an order that was refused");

        (await fixture.ReadItemAsync(plentiful))!.Reserved.Should().Be(0);
        (await fixture.ReadItemAsync(plentiful))!.QuantityOnHand.Should().Be(10);
        (await fixture.ReadItemAsync(scarce))!.Reserved.Should().Be(0);
        (await fixture.ReadItemAsync(scarce))!.QuantityOnHand.Should().Be(1);

        var outcome = await fixture.ConsumeOutcomeAsync();

        outcome.RoutingKey.Should().Be(StockEventTopology.ReservationRejectedRoutingKey);

        var rejected = JsonSerializer.Deserialize<StockReservationRejected>(
            outcome.Body, StockMessageJson.Options);

        rejected!.Reason.Should().Be(RejectionReason.InsufficientStock);
        rejected.FailedProductId.Should().Be(scarce,
            "the saga needs to know which line to act on, not only that something was short");
    }

    /// <summary>
    /// Polls a condition rather than sleeping a guessed duration. Fails with the caller's reason, so a
    /// timeout names what did not happen instead of only that something did not.
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow.Add(WaitTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"Timed out after {WaitTimeout.TotalSeconds:F0}s: {because}.");
    }
}
