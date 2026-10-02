using System.Net;
using System.Net.Http.Json;
using AgenticShop.Stock.Contracts;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// The reason Stock exists as a service with its own concurrency token: nothing here can be
/// proven by a unit test or against a mocked context. Whether EF emits
/// <c>WHERE xmin = @original</c>, and whether PostgreSQL honours it, is only observable against
/// a real database under real contention.
/// </summary>
/// <remarks>
/// <para>
/// The parallel assertions are deliberately inequalities. Under contention a request that read a
/// stale row version fails on its xmin token even when stock was still available, and Phase 0
/// does not retry server-side — so the number of successes depends on how the requests
/// interleaved. What must hold every time is that stock is never oversold, that the counters
/// agree with the reservations, and that every rejection is a 4xx rather than a 500.
/// Asserting an exact success count here would be flaky and would test the scheduler, not the
/// design. The deterministic boundary is covered separately, sequentially.
/// </para>
/// </remarks>
[Collection("Stock API")]
public sealed class OversellingConcurrencyTests(StockApiFixture fixture) : IAsyncLifetime
{
    private const string StockUrl = "/api/v1/stock";
    private const string ReservationsUrl = "/api/v1/reservations";

    private HttpClient Client => fixture.Client;

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AParallelBurstNeverOversells()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);

        // 25 concurrent holds of one unit each, every one for a different order, against 10
        // units. Each request gets its own scoped DbContext, exactly as in production.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 25)
                .Select(_ => Client.PostAsJsonAsync(
                    $"{StockUrl}/{productId}/reservations",
                    new ReserveStockRequest(Guid.NewGuid(), 1))));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        var successes = statuses.Count(s => s == HttpStatusCode.Created);
        var conflicts = statuses.Count(s => s == HttpStatusCode.Conflict);

        // A contention burst must not surface as a server fault. This is the assertion that
        // would catch a misclassified DbUpdateConcurrencyException or an unmapped SQLSTATE, so it
        // runs first: the counts below would also fail on a 5xx, and would report it as a
        // mismatched tally rather than as the server fault it actually was.
        responses.Should().NotContain(r => (int)r.StatusCode >= 500,
            "contention is a client-visible conflict, never a 5xx");

        // The headline invariant.
        successes.Should().BeLessThanOrEqualTo(10, "ten units cannot satisfy more than ten holds");

        successes.Should().BeGreaterThan(0, "at least one request should win the race");
        (successes + conflicts).Should().Be(25, "every request must be answered");

        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(10, "reserving never ships");
        stock.Reserved.Should().Be(successes, "the counter must agree with how many holds actually committed");
        stock.Reserved.Should().BeLessThanOrEqualTo(stock.QuantityOnHand);
        stock.Available.Should().Be(10 - successes);

        await AssertReservationCountAsync(productId, successes);
    }

    [Fact]
    public async Task TheExactBoundaryIsEnforcedSequentially()
    {
        // Deterministic counterpart to the burst above: with no contention the domain rule is
        // exact, so the eleventh hold must fail and the first ten must not.
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var response = await Client.PostAsJsonAsync(
                $"{StockUrl}/{productId}/reservations",
                new ReserveStockRequest(Guid.NewGuid(), 1));

            response.StatusCode.Should().Be(HttpStatusCode.Created, $"hold {attempt} of 10 should succeed");
        }

        var eleventh = await Client.PostAsJsonAsync(
            $"{StockUrl}/{productId}/reservations",
            new ReserveStockRequest(Guid.NewGuid(), 1));

        eleventh.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var stock = await GetStockAsync(productId);

        stock.Reserved.Should().Be(10);
        stock.Available.Should().Be(0);
        stock.QuantityOnHand.Should().Be(10);
    }

    [Fact]
    public async Task ParallelConfirmsOfOneReservationShipTheGoodsExactlyOnce()
    {
        // The double-ship risk. If the state machine or the concurrency token slipped, several
        // confirms would each subtract from on-hand.
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);
        var reservation = await CreateReservationAsync(productId, 4);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/confirm", content: null)));

        var statuses = responses.Select(r => r.StatusCode).ToList();

        // The no-5xx assertion comes first because it is the diagnostic one. An exact 409 count
        // placed ahead of it once turned a genuine server fault into a report of "8 instead of 9",
        // which named the symptom and hid the cause.
        responses.Should().NotContain(r => (int)r.StatusCode >= 500,
            "contention is a client-visible conflict, never a server fault");

        // Only two outcomes are reachable: the state machine settles one and refuses the rest, or the
        // xmin token refuses them. Anything else — a 400, a 404, a 422 — is a misclassification, and
        // this catches it without depending on how the requests interleaved.
        //
        // Written as equality rather than an `is` pattern: FluentAssertions builds an expression
        // tree here, and pattern matching is not allowed inside one.
        statuses.Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict,
            "a parallel confirm either settles or conflicts");

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1,
            "exactly one confirm may settle the reservation");

        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(6, "four units shipped once, not four units shipped repeatedly");
        stock.Reserved.Should().Be(0);
    }

    [Fact]
    public async Task ParallelReleasesOfOneReservationLiftTheHoldExactlyOnce()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);
        var reservation = await CreateReservationAsync(productId, 4);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => Client.PostAsync($"{ReservationsUrl}/{reservation.Id}/release", content: null)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Should().NotContain(r => (int)r.StatusCode >= 500);

        var stock = await GetStockAsync(productId);

        stock.QuantityOnHand.Should().Be(10, "releasing ships nothing");
        stock.Reserved.Should().Be(0);
        stock.Available.Should().Be(10, "the hold is lifted once, not repeatedly");
    }

    [Fact]
    public async Task AStaleWriterIsRejectedByTheXminToken()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);

        // Two independent units of work reading the same row, as two overlapping requests would.
        using var firstScope = fixture.CreateScope();
        using var secondScope = fixture.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<StockDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<StockDbContext>();

        var firstItem = await first.StockItems.SingleAsync(s => s.ProductId == productId);
        var secondItem = await second.StockItems.SingleAsync(s => s.ProductId == productId);

        firstItem.Reserve(3);
        await first.SaveChangesAsync();

        secondItem.Reserve(3);

        // Stock was sufficient for both, so only the xmin token stops the second writer. This is
        // the exact case where a missing token would silently oversell.
        var staleWrite = () => second.SaveChangesAsync();

        await staleWrite.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the row's xmin changed when the first writer committed");
    }

    [Fact]
    public async Task TheFirstWriterSurvivesAConflict()
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);

        using var firstScope = fixture.CreateScope();
        using var secondScope = fixture.CreateScope();
        using var verifyScope = fixture.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<StockDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<StockDbContext>();

        var firstItem = await first.StockItems.SingleAsync(s => s.ProductId == productId);
        var secondItem = await second.StockItems.SingleAsync(s => s.ProductId == productId);

        firstItem.Reserve(3);
        await first.SaveChangesAsync();

        secondItem.Reserve(4);

        try
        {
            await second.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Expected — the assertions below are what matter.
        }

        var persisted = await verifyScope.ServiceProvider
            .GetRequiredService<StockDbContext>()
            .StockItems
            .SingleAsync(s => s.ProductId == productId);

        persisted.Reserved.Should().Be(3, "the losing writer must leave no trace");
        persisted.QuantityOnHand.Should().Be(10);
    }

    [Fact]
    public async Task SequentialWritesBothSucceed()
    {
        // Guards against the token being wired so tightly that ordinary serial updates fail.
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);

        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        var item = await db.StockItems.SingleAsync(s => s.ProductId == productId);

        item.Reserve(2);
        await db.SaveChangesAsync();

        item.Reserve(3);
        var secondWrite = () => db.SaveChangesAsync();

        await secondWrite.Should().NotThrowAsync();

        (await db.StockItems.SingleAsync(s => s.ProductId == productId)).Reserved.Should().Be(5);
    }

    [Fact]
    public async Task AReservationAndItsCounterCommitAtomically()
    {
        // The transaction boundary: a committed hold must always have a matching reservation row,
        // and a rejected one must leave neither. This is also the property Phase 2's outbox will
        // depend on — an outbox row added to this context would commit with both.
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 5);

        await CreateReservationAsync(productId, 5);

        var rejected = await Client.PostAsJsonAsync(
            $"{StockUrl}/{productId}/reservations",
            new ReserveStockRequest(Guid.NewGuid(), 1));

        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var stock = await GetStockAsync(productId);

        stock.Reserved.Should().Be(5);
        await AssertReservationCountAsync(productId, 1);

        var totalHeld = await SumReservationQuantityAsync(productId);

        totalHeld.Should().Be(stock.Reserved,
            "the counter and the reservation rows are written in one transaction and must agree");
    }

    private async Task AssertReservationCountAsync(Guid productId, int expected)
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        var count = await (
            from reservation in db.StockReservations.AsNoTracking()
            join item in db.StockItems.AsNoTracking() on reservation.StockItemId equals item.Id
            where item.ProductId == productId
            select reservation).CountAsync();

        count.Should().Be(expected);
    }

    private async Task<int> SumReservationQuantityAsync(Guid productId)
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        return await (
            from reservation in db.StockReservations.AsNoTracking()
            join item in db.StockItems.AsNoTracking() on reservation.StockItemId equals item.Id
            where item.ProductId == productId && reservation.Status == ReservationStatus.Pending
            select reservation.Quantity).SumAsync();
    }

    private async Task<StockResponse> GetStockAsync(Guid productId)
        => await StockApiTests.ReadJsonAsync<StockResponse>(await Client.GetAsync($"{StockUrl}/{productId}"));

    private async Task SeedStockAsync(Guid productId, int quantityOnHand)
    {
        var response = await Client.PostAsJsonAsync(StockUrl, new SetStockRequest(productId, quantityOnHand));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the stock record should have been created");
    }

    private async Task<ReservationResponse> CreateReservationAsync(Guid productId, int quantity)
    {
        var response = await Client.PostAsJsonAsync(
            $"{StockUrl}/{productId}/reservations",
            new ReserveStockRequest(Guid.NewGuid(), quantity));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the reservation should have been created");

        return await StockApiTests.ReadJsonAsync<ReservationResponse>(response);
    }
}
