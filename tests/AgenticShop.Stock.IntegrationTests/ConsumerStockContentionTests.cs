using System.Net.Http.Json;
using AgenticShop.Stock.Contracts;
using AgenticShop.Stock.Domain;
using AgenticShop.Stock.Messaging;
using FluentAssertions;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// The contention that survives Phase 2: the <see cref="OrderPlacedConsumer"/> reserving-and-confirming
/// an order against <c>PUT /api/v1/stock/{productId}</c> replacing the physical count, both writing the
/// same <c>stock_items</c> row under the same <c>xmin</c> token.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the deleted <c>OversellingConcurrencyTests</c>, which raced the synchronous HTTP
/// reserve endpoint against itself. That endpoint is gone — the consumer is now the only writer that
/// holds stock, and it does so inline (reserve and confirm in one transaction) and serially
/// (<c>prefetchCount: 1</c>, <c>consumerDispatchConcurrency: 1</c>), so it never races itself. The one
/// remaining cross-writer race on the counter is consumer-versus-PUT, and that is what this covers.
/// </para>
/// <para>
/// The assertions are inequalities and equalities that must hold under <i>any</i> interleaving, never
/// exact counts (§6). A message that loses the <c>xmin</c> race is requeued once and, if it loses again,
/// dropped without an inbox row — so how many orders commit depends on the scheduler, and asserting a
/// number would test the scheduler rather than the design. What must hold every time: no request 5xxs,
/// the host keeps consuming, <c>reserved</c> never leaks (reserve-and-confirm is one transaction, so a
/// committed order leaves it where it started and a rolled-back one leaves nothing), the counter agrees
/// with the reservation rows, and stock is never oversold.
/// </para>
/// </remarks>
[Collection("Stock Messaging")]
public sealed class ConsumerStockContentionTests(StockMessagingFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private const string StockUrl = "/api/v1/stock";

    /// <summary>
    /// Generous, and PUT keeps restoring it, so no burst message is ever refused for insufficient stock.
    /// That keeps the outcome binary — a message either commits (a <c>Confirmed</c> row) or is dropped on
    /// a lost race (no row) — which is what makes the counter-versus-rows equality below a real guard
    /// rather than one blurred by legitimate rejections.
    /// </summary>
    private const int SeedQuantity = 1000;

    private const int MessageCount = 8;
    private const int UpdateCount = 8;

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheConsumerAndThePhysicalCountUpdateNeverOversellOrLeakAReservation()
    {
        var productId = Guid.NewGuid();
        await fixture.SeedStockAsync(productId, SeedQuantity);

        // Two writers, one row, fired at once. The publishes enqueue OrderPlaced messages the consumer
        // works through serially; the PUTs replace the physical count over HTTP. Both start before
        // either is awaited, so they overlap on the row and on its xmin version.
        var publishTasks = Enumerable.Range(0, MessageCount)
            .Select(i => fixture.PublishOrderPlacedAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                $"ORD-RACE-{i:D4}",
                [new OrderPlacedLine(productId, 2)]))
            .Cast<Task>()
            .ToArray();

        var updateTasks = Enumerable.Range(0, UpdateCount)
            .Select(_ => fixture.Client.PutAsJsonAsync(
                $"{StockUrl}/{productId}",
                new UpdateStockRequest(SeedQuantity)))
            .ToArray();

        await Task.WhenAll(publishTasks);

        var updateResponses = await Task.WhenAll(updateTasks);

        // Runs first because it is the diagnostic one: a misclassified DbUpdateConcurrencyException or an
        // unmapped SQLSTATE would surface here as a 5xx, and the state assertions below would otherwise
        // report it as a mismatched counter rather than as the server fault it was. A conflict is a 409
        // and a below-reserved value is a 400 — contention is client-visible, never a fault.
        updateResponses.Should().NotContain(r => (int)r.StatusCode >= 500,
            "losing the xmin race is a 409 and setting below reserved is a 400, never a 5xx");

        // The PUTs are done, so the consumer is now the only writer and there is no further contention.
        // A canary published afterwards is therefore guaranteed to be processed rather than dropped, and
        // with serial dispatch its inbox row proves every burst message before it was settled too. This
        // is completion by ordering, not by a guessed delay.
        var canaryId = Guid.NewGuid();

        await fixture.PublishOrderPlacedAsync(
            canaryId,
            Guid.NewGuid(),
            "ORD-RACE-CANARY",
            [new OrderPlacedLine(productId, 1)]);

        await WaitUntilAsync(
            async () => (await fixture.ReadInboxAsync()).Any(m => m.MessageId == canaryId),
            "the consumer should have drained the burst and processed the canary, proving the host " +
            "survived the contention and is still consuming");

        var item = await fixture.ReadItemAsync(productId);

        item.Should().NotBeNull();

        // The headline invariants. Reserve-and-confirm is one transaction, so nothing in this path can
        // park a non-zero reserved at rest: a committed order nets it back to where it started and a
        // rolled-back one leaves no trace. A non-zero reserved here would mean a held unit outlived the
        // delivery that held it — exactly the leak the single-transaction design exists to prevent.
        item!.Reserved.Should().Be(0,
            "reserve-and-confirm commits atomically, so no hold can outlive its delivery");

        item.Reserved.Should().BeLessThanOrEqualTo(item.QuantityOnHand, "never oversell");
        item.Available.Should().BeGreaterThanOrEqualTo(0, "available is on-hand minus reserved");
        item.QuantityOnHand.Should().BeGreaterThanOrEqualTo(0);
        item.QuantityOnHand.Should().BeLessThanOrEqualTo(SeedQuantity,
            "PUT only ever restores the seed value and the consumer only decrements, so on-hand cannot " +
            "exceed it — a higher count would mean a shipment was double-counted back into inventory");

        // The counter agrees with the committed rows. Reserved equals the sum still Pending, and the
        // consumer never leaves a row Pending, so both are zero. A leak in either direction — a reserved
        // unit with no row behind it, or a Pending row with no reserved unit — breaks the equality. This
        // is the property the deleted AReservationAndItsCounterCommitAtomically pinned for the HTTP path.
        var reservations = await fixture.ReadReservationsAsync();

        var pendingSum = reservations
            .Where(r => r.Status == ReservationStatus.Pending)
            .Sum(r => r.Quantity);

        pendingSum.Should().Be(item.Reserved,
            "the reserved counter and the reservation rows are written in one transaction and must agree");

        // Non-vacuity: the burst did real work. The canary committed once contention stopped, so there is
        // at least one row, and every row this path writes is Confirmed — a Pending or Released row would
        // mean a partial write escaped the single transaction.
        reservations.Should().NotBeEmpty("the consumer should have committed at least the canary order");
        reservations.Should().OnlyContain(r => r.Status == ReservationStatus.Confirmed,
            "this path confirms inline; any other status is a partial write that escaped");

        var inbox = await fixture.ReadInboxAsync();

        inbox.Should().ContainSingle(m => m.MessageId == canaryId,
            "the canary is processed exactly once");
    }

    /// <summary>
    /// Polls a condition rather than sleeping a guessed duration, and fails with the caller's reason so a
    /// timeout names what did not happen. Copied from the sibling consumer suite: the three integration
    /// assemblies must not reference each other, and a shared helper would be a fourth thing to keep in
    /// step for one twelve-line method.
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
