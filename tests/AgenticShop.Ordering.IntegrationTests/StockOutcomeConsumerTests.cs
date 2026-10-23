using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.Messaging;
using FluentAssertions;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// That an outcome from Stock settles the order it answers, exactly once, and that a second outcome
/// for a settled order changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// Run against a real broker with <see cref="StockOutcomeConsumer"/> registered by the fixture rather
/// than by <c>Program.cs</c>. The orders under test are seeded as <c>Pending</c> straight into the
/// database, because the endpoint still runs the synchronous path and cannot produce that state — see
/// <see cref="OrderingMessagingFixture.SeedPendingOrderAsync"/>.
/// </para>
/// <para>
/// <b>Proving that nothing happened needs an ordering argument, not a timeout.</b> Two of these tests
/// assert that a delivery was ignored, and "wait three seconds and check" would be a guess about
/// latency that passes for the wrong reason on a slow machine. Instead each publishes a second,
/// distinct outcome afterwards and waits on that: with <c>prefetchCount</c> 1 and serial dispatch, if
/// the later delivery was processed then every delivery before it was too.
/// </para>
/// </remarks>
[Collection("Ordering Messaging")]
public sealed class StockOutcomeConsumerTests(OrderingMessagingFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A W3C traceparent, well-formed and recognisable, used to prove it survives the hop.</summary>
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task StockCommittedSettlesAPendingOrderAsConfirmed()
    {
        var productId = Guid.NewGuid();
        var order = await fixture.SeedPendingOrderAsync(productId, quantity: 2);

        order.Status.Should().Be(OrderStatus.Pending);
        order.UpdatedAtUtc.Should().BeNull("nothing has changed the row since it was written");

        var messageId = Guid.NewGuid();

        await fixture.PublishStockCommittedAsync(
            messageId,
            order.Id,
            [new StockCommittedLine(productId, 2)],
            TraceParent);

        await WaitUntilAsync(
            async () => (await fixture.ReadOrderAsync(order.Id))!.IsTerminal,
            "the outcome should have settled the order");

        var settled = await fixture.ReadOrderAsync(order.Id);

        settled!.Status.Should().Be(OrderStatus.Confirmed);
        settled.UpdatedAtUtc.Should().NotBeNull("settling an order is an update, and an update with no timestamp is unauditable");
        settled.UpdatedAtUtc!.Value.Should().BeOnOrAfter(settled.PlacedAtUtc);
        settled.Lines.Should().ContainSingle("the transition must not touch the snapshot");

        var inbox = await fixture.ReadInboxAsync();

        inbox.Should().ContainSingle();
        inbox[0].MessageId.Should().Be(messageId);
        inbox[0].EventType.Should().Be(StockEventTopology.CommittedRoutingKey,
            "the row records which event settled the order, which is what makes a later contradiction diagnosable");
    }

    [Fact]
    public async Task StockReservationRejectedSettlesAPendingOrderAsFailed()
    {
        var productId = Guid.NewGuid();
        var order = await fixture.SeedPendingOrderAsync(productId, quantity: 5);

        var messageId = Guid.NewGuid();

        await fixture.PublishStockReservationRejectedAsync(
            messageId,
            order.Id,
            RejectionReason.InsufficientStock,
            productId);

        await WaitUntilAsync(
            async () => (await fixture.ReadOrderAsync(order.Id))!.IsTerminal,
            "the rejection should have settled the order");

        var settled = await fixture.ReadOrderAsync(order.Id);

        settled!.Status.Should().Be(OrderStatus.Failed);
        settled.UpdatedAtUtc.Should().NotBeNull();

        // All-or-nothing on the producing side means there is no partial outcome to represent, which
        // is why O14's confirm-phase policy has no equivalent on this path.
        settled.Status.Should().NotBe(OrderStatus.PartiallyConfirmed,
            "Stock holds every line or none, so the event path cannot produce a partial settlement");

        (await fixture.ReadInboxAsync()).Should().ContainSingle(m => m.MessageId == messageId);
    }

    [Fact]
    public async Task ARedeliveredOutcomeSettlesTheOrderOnce()
    {
        var productId = Guid.NewGuid();
        var order = await fixture.SeedPendingOrderAsync(productId, quantity: 2);

        var messageId = Guid.NewGuid();
        var lines = new[] { new StockCommittedLine(productId, 2) };

        await fixture.PublishStockCommittedAsync(messageId, order.Id, lines);

        await WaitUntilAsync(
            async () => (await fixture.ReadOrderAsync(order.Id))!.IsTerminal,
            "the first delivery should have settled the order");

        var firstSettlement = (await fixture.ReadOrderAsync(order.Id))!;

        // The same message again — what a producer whose outbox row was lost after the broker
        // acknowledged it would send.
        await fixture.PublishStockCommittedAsync(messageId, order.Id, lines);

        await PublishCanaryAndWaitAsync();

        var after = await fixture.ReadOrderAsync(order.Id);

        after!.Status.Should().Be(OrderStatus.Confirmed);
        after.UpdatedAtUtc.Should().Be(firstSettlement.UpdatedAtUtc,
            "a redelivery must not settle the order a second time, and must not even touch its timestamp");

        (await fixture.ReadInboxAsync())
            .Count(message => message.MessageId == messageId)
            .Should().Be(1, "one message, one inbox row, however many times it is delivered");
    }

    /// <summary>
    /// The case the inbox exists to make visible: a <b>different</b> message for an order that has
    /// already settled.
    /// </summary>
    /// <remarks>
    /// Without the inbox this is indistinguishable from a redelivery — both present as "an outcome
    /// arrived and the order was already terminal". With it, the message id answers the question, and
    /// the two get different log severities: a redelivery is ordinary at-least-once behaviour, while
    /// two outcomes for one <c>OrderPlaced</c> means Stock published twice and someone should look.
    /// This test asserts the state, which is the part that must not change either way.
    /// </remarks>
    [Fact]
    public async Task AContradictorySecondOutcomeForASettledOrderChangesNothing()
    {
        var productId = Guid.NewGuid();
        var order = await fixture.SeedPendingOrderAsync(productId, quantity: 2);

        var settlingMessageId = Guid.NewGuid();

        await fixture.PublishStockCommittedAsync(
            settlingMessageId, order.Id, [new StockCommittedLine(productId, 2)]);

        await WaitUntilAsync(
            async () => (await fixture.ReadOrderAsync(order.Id))!.IsTerminal,
            "the reservation outcome should have settled the order");

        var settled = (await fixture.ReadOrderAsync(order.Id))!;

        // A fresh message id, and the opposite outcome. Stock cannot produce this — it publishes one
        // outcome per OrderPlaced — so it stands in for a producer bug.
        var contradictoryMessageId = Guid.NewGuid();

        await fixture.PublishStockReservationRejectedAsync(
            contradictoryMessageId, order.Id, RejectionReason.UnknownProduct, productId);

        await PublishCanaryAndWaitAsync();

        var after = await fixture.ReadOrderAsync(order.Id);

        after!.Status.Should().Be(OrderStatus.Confirmed,
            "an order settles once; a later contradictory event is refused rather than applied");
        after.UpdatedAtUtc.Should().Be(settled.UpdatedAtUtc);

        // Asserted per message id rather than as a count of the whole table: the canary below settles
        // an order of its own and writes an inbox row of its own, so the inbox is not expected to hold
        // one row. What matters is which two ids are and are not in it.
        var inbox = await fixture.ReadInboxAsync();

        inbox.Should().ContainSingle(message => message.MessageId == settlingMessageId,
            "the outcome that settled the order is recorded once");

        inbox.Should().NotContain(message => message.MessageId == contradictoryMessageId,
            "the contradictory message was not applied, so it must not be recorded as applied either — " +
            "that row is what lets a later reader tell a redelivery from a producer bug");
    }

    /// <summary>
    /// Settles a throwaway order and waits for it, which proves every delivery published before this
    /// one has been processed. Cheaper and more reliable than sleeping.
    /// </summary>
    private async Task PublishCanaryAndWaitAsync()
    {
        var canaryProduct = Guid.NewGuid();
        var canary = await fixture.SeedPendingOrderAsync(canaryProduct, quantity: 1);

        await fixture.PublishStockCommittedAsync(
            Guid.NewGuid(), canary.Id, [new StockCommittedLine(canaryProduct, 1)]);

        await WaitUntilAsync(
            async () => (await fixture.ReadOrderAsync(canary.Id))!.IsTerminal,
            "the canary outcome should have settled, which with prefetch 1 and serial dispatch means " +
            "every delivery published before it was processed too");
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
