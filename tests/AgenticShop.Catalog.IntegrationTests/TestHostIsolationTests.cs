using AgenticShop.Catalog.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Catalog.IntegrationTests;

/// <summary>
/// Guards the test harness rather than the API.
/// </summary>
/// <remarks>
/// If the connection-string override in <see cref="CatalogApiFactory"/> ever stops taking
/// effect, every other test in this project silently runs against the developer's compose
/// database: they still pass, while truncating and writing real local data, and they fail
/// on any machine where that database does not exist. This happened once already, when the
/// connection string was read eagerly in Program.cs before the host had applied its
/// configuration overrides.
/// </remarks>
[Collection("Catalog API")]
public sealed class TestHostIsolationTests(CatalogApiFixture fixture)
{
    [Fact]
    public async Task TheApiUnderTestUsesTheTestcontainersDatabase()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";

        var database = (string?)await command.ExecuteScalarAsync();

        database.Should().Be(CatalogApiFixture.TestDatabaseName);
    }

    [Fact]
    public async Task TheApiUnderTestDoesNotUseTheComposeDatabase()
    {
        // The compose database is reachable on this machine whenever `docker compose up`
        // has run, which is what made the original mistake invisible. Assert the negative
        // explicitly so a future regression cannot hide behind a passing test suite.
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var connectionString = db.Database.GetConnectionString();

        connectionString.Should().NotBeNull();
        connectionString.Should().Contain(CatalogApiFixture.TestDatabaseName);
        connectionString.Should().NotContain("catalog_svc", "that is the compose role, not the test container's");
    }
}
