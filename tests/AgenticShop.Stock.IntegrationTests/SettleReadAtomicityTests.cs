using System.Net;
using System.Net.Http.Json;
using AgenticShop.Stock.Contracts;
using FluentAssertions;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Pins the read shape that keeps a settle conflict a 409 rather than a 500.
/// </summary>
/// <remarks>
/// <para>
/// <c>SettleAsync</c> used to read the reservation and its stock item in two separate queries.
/// Under READ COMMITTED each statement takes its own snapshot, so a winner committing between them
/// left the loser holding a still-<c>Pending</c> reservation next to an item whose
/// <c>Reserved</c> had already dropped to zero. The state machine passed on the stale half, the
/// counter guard threw <c>InvalidOperationException</c> on the fresh half, and because that happens
/// before <c>SaveChangesAsync</c> the <c>xmin</c> token never ran — so an ordinary conflict reached
/// the caller as a 500. Measured on a live container: 7 in 10,000 parallel confirms, 6 in 10,000
/// parallel releases, with the counters correct in every single case.
/// </para>
/// <para>
/// One statement means one snapshot, which closes the window: the loser either reads an
/// already-settled reservation and gets the state machine's 409, or reads both rows pre-commit and
/// gets the <c>xmin</c> 409 at save time. There is no third outcome.
/// </para>
/// <para>
/// Counting statements is the only deterministic way to hold that line. At roughly 7 in 10,000, a
/// ten-request parallel burst sees the failure about once in a hundred runs — which is precisely how
/// it survived five consecutive green full-solution runs while being blamed on a deadlock that was
/// never reachable.
/// </para>
/// </remarks>
[Collection("Stock API")]
public sealed class SettleReadAtomicityTests(StockApiFixture fixture) : IAsyncLifetime
{
    private const string StockUrl = "/api/v1/stock";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("confirm")]
    [InlineData("release")]
    public async Task SettlingReadsTheReservationAndItsStockItemInOneStatement(string settle)
    {
        var productId = Guid.NewGuid();
        await SeedStockAsync(productId, quantityOnHand: 10);
        var reservation = await CreateReservationAsync(productId, quantity: 4);

        IReadOnlyList<string> statements = [];
        HttpResponseMessage response;

        fixture.Sql.Begin();

        try
        {
            response = await fixture.Client.PostAsync(
                $"/api/v1/reservations/{reservation.Id}/{settle}",
                content: null);

            statements = fixture.Sql.Statements;
        }
        finally
        {
            fixture.Sql.End();
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an uncontended {0} must succeed", settle);

        statements.Should().NotBeEmpty(
            "the capture must observe the host's SQL, or every assertion below passes vacuously");

        var selects = statements
            .Where(s => s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        selects.Should().HaveCount(1,
            "the settle path must read the reservation and its stock item in one statement, so both " +
            "come from one snapshot; two reads let a winner commit in between and turn the loser's " +
            "conflict into a 500. Statements issued: {0}",
            string.Join(" | ", statements.Select(FirstLine)));

        selects[0].Should().Contain("stock_reservations").And.Contain("stock_items",
            "the single statement must be the one that fetches both rows");

        selects[0].Should().Contain("LEFT JOIN",
            "an inner join would report a reservation whose stock item is missing — data damage the " +
            "RESTRICT foreign key makes unreachable — as the caller's 404 instead of a 500");
    }

    private static string FirstLine(string sql)
    {
        var line = sql.Split('\n')[0].Trim();

        return line.Length <= 90 ? line : string.Concat(line.AsSpan(0, 90), "...");
    }

    private async Task SeedStockAsync(Guid productId, int quantityOnHand)
    {
        var response = await fixture.Client.PostAsJsonAsync(
            StockUrl,
            new SetStockRequest(productId, quantityOnHand));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the stock record should have been created");
    }

    private async Task<ReservationResponse> CreateReservationAsync(Guid productId, int quantity)
    {
        var response = await fixture.Client.PostAsJsonAsync(
            $"{StockUrl}/{productId}/reservations",
            new ReserveStockRequest(Guid.NewGuid(), quantity));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the reservation should have been created");

        var reservation = await response.Content.ReadFromJsonAsync<ReservationResponse>();

        reservation.Should().NotBeNull("a 201 from reserve always carries the reservation");

        return reservation!;
    }
}
