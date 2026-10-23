using AgenticShop.Stock.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// One real PostgreSQL container per test run, shared by every test in the collection. A real
/// database rather than an EF mock is the point: only PostgreSQL can raise the unique violation
/// behind a duplicate-hold 409, enforce the CHECK constraints, apply the xmin concurrency token
/// or translate the reservation join.
/// </summary>
public sealed class StockApiFixture : IAsyncLifetime
{
    /// <summary>
    /// Exposed so TestHostIsolationTests can prove the app really is talking to this container
    /// rather than to a developer's compose database.
    /// </summary>
    public const string TestDatabaseName = "agenticshop_stock_tests";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestPostgreSql.Image)
        .WithDatabase(TestDatabaseName)
        .WithUsername("agenticshop")
        .WithPassword("agenticshop_test")
        .Build();

    private StockApiFactory? _factory;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// Exposes the host's services so a test can open independent units of work against the same
    /// database — which is what an optimistic-concurrency conflict needs.
    /// </summary>
    public IServiceScope CreateScope() => _factory!.Services.CreateScope();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _factory = new StockApiFactory(_postgres.GetConnectionString());
        Client = _factory.CreateClient();
    }

    /// <summary>
    /// Tests share a database, so each one starts from empty rather than depending on execution
    /// order. Reservations go first: the foreign key to stock_items is RESTRICT.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        await db.StockReservations.ExecuteDeleteAsync();
        await db.StockItems.ExecuteDeleteAsync();
    }

    /// <summary>
    /// Disposal is exception-safe. Catalog's fixture disposes in sequence without a guard, so a
    /// throw from the factory leaves the container running until Ryuk reaps it; that is tracked
    /// in <c>docs/KNOWN-ISSUES.md</c> and was not copied forward.
    /// </summary>
    public async Task DisposeAsync()
    {
        try
        {
            Client.Dispose();

            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }
        }
        finally
        {
            await _postgres.DisposeAsync();
        }
    }
}
