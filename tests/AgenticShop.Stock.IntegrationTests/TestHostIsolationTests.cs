using AgenticShop.Stock.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Guards the test harness rather than the API.
/// </summary>
/// <remarks>
/// If the connection-string override in <see cref="StockApiFactory"/> ever stops taking effect,
/// every other test in this project silently runs against the developer's compose database: they
/// still pass, while truncating and writing real local data, and they fail on any machine where
/// that database does not exist. This happened once in Catalog, when the connection string was
/// read eagerly in Program.cs before the host had applied its configuration overrides.
/// </remarks>
[Collection("Stock API")]
public sealed class TestHostIsolationTests(StockApiFixture fixture)
{
    [Fact]
    public async Task TheApiUnderTestUsesTheTestcontainersDatabase()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";

        var database = (string?)await command.ExecuteScalarAsync();

        database.Should().Be(StockApiFixture.TestDatabaseName);
    }

    [Fact]
    public void TheApiUnderTestDoesNotUseTheComposeDatabase()
    {
        // The compose database is reachable whenever `docker compose up` has run, which is what
        // made the original mistake invisible. Assert the negative explicitly.
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        var connectionString = db.Database.GetConnectionString();

        connectionString.Should().NotBeNull();
        connectionString.Should().Contain(StockApiFixture.TestDatabaseName);
        connectionString.Should().NotContain("stock_svc", "that is the compose role, not the test container's");
    }

    [Fact]
    public async Task TheTestDatabaseCarriesTheMigratedSchema()
    {
        // Program.cs migrates only under IsDevelopment(), so this also proves the factory's
        // explicit UseEnvironment is taking effect.
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name IN ('stock_items', 'stock_reservations')";

        var tables = (long)(await command.ExecuteScalarAsync())!;

        tables.Should().Be(2);
    }
}
