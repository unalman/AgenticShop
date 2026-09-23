using AgenticShop.Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AgenticShop.Catalog.IntegrationTests;

/// <summary>
/// One real PostgreSQL container per test run, shared by every test in the collection.
/// Using a real database rather than an EF mock is the point: it exercises migrations,
/// the unique SKU index, query filters and SQL translation — all things a mock hides.
/// </summary>
public sealed class CatalogApiFixture : IAsyncLifetime
{
    /// <summary>
    /// Exposed so TestHostIsolationTests can prove the app really is talking to this
    /// container rather than to a developer's compose database.
    /// </summary>
    public const string TestDatabaseName = "agenticshop_catalog_tests";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestPostgreSql.Image)
        .WithDatabase(TestDatabaseName)
        .WithUsername("agenticshop")
        .WithPassword("agenticshop_test")
        .Build();

    private CatalogApiFactory? _factory;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// Exposes the host's services so a test can open independent units of work against
    /// the same database — which is what an optimistic-concurrency conflict needs.
    /// </summary>
    public IServiceScope CreateScope() => _factory!.Services.CreateScope();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _factory = new CatalogApiFactory(_postgres.GetConnectionString());
        Client = _factory.CreateClient();
    }

    /// <summary>
    /// Tests share a database, so each one starts from empty rather than depending
    /// on execution order.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        await db.Products.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
