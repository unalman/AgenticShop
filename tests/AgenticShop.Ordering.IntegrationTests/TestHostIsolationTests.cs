using AgenticShop.Ordering.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Guards the test harness rather than the API.
/// </summary>
/// <remarks>
/// If the connection-string override in <see cref="OrderingApiFactory"/> ever stops taking effect,
/// every other test in this project silently runs against the developer's compose database: they
/// still pass, while truncating and writing real local data, and they fail on any machine where that
/// database does not exist. This happened once in Catalog, when the connection string was read
/// eagerly in Program.cs before the host had applied its configuration overrides. A third copy of the
/// guard, for the same reason as the other two.
/// </remarks>
[Collection("Ordering API")]
public sealed class TestHostIsolationTests(OrderingApiFixture fixture)
{
    [Fact]
    public async Task TheApiUnderTestUsesTheTestcontainersDatabase()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";

        var database = (string?)await command.ExecuteScalarAsync();

        database.Should().Be(OrderingApiFixture.TestDatabaseName);
    }

    [Fact]
    public void TheApiUnderTestDoesNotUseTheComposeDatabase()
    {
        // The compose database is reachable whenever `docker compose up` has run, which is what made
        // the original mistake invisible. Assert the negative explicitly.
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var connectionString = db.Database.GetConnectionString();

        connectionString.Should().NotBeNull();
        connectionString.Should().Contain(OrderingApiFixture.TestDatabaseName);
        connectionString.Should().NotContain("ordering_svc", "that is the compose role, not the test container's");
    }

    [Fact]
    public async Task TheTestDatabaseCarriesTheMigratedSchema()
    {
        // Program.cs migrates only under IsDevelopment(), so this also proves the factory's explicit
        // UseEnvironment is taking effect.
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name IN ('orders', 'order_lines')";

        var tables = (long)(await command.ExecuteScalarAsync())!;

        tables.Should().Be(2);
    }

    [Fact]
    public void TheMigratedSchemaHasNoXminColumn()
    {
        // Proof rather than documentation: Npgsql maps a uint concurrency token generated
        // OnAddOrUpdate onto the system column and suppresses the DDL, so xmin must appear in the
        // model but never in information_schema.
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        db.Model.GetEntityTypes()
            .Should().OnlyContain(entity => entity.FindProperty("xmin") != null);

        var connection = db.Database.GetDbConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND column_name = 'xmin'";

        ((long)command.ExecuteScalar()!).Should().Be(0);
    }

    [Fact]
    public void TheHostRefusesToStartWithoutADownstreamBaseUrl()
    {
        // Ordering is the first service with a required non-connection-string setting. A typed client
        // with no base address fails on the first outbound call — which is the middle of a placement,
        // after stock has been reserved — so refusing to start is strictly better.
        using var factory = new OrderingApiFactory(fixture.ConnectionString, catalogBaseUrl: string.Empty);

        var act = () => _ = factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Services:Catalog:BaseUrl*");
    }

    [Fact]
    public void TheHostRefusesToStartWithADownstreamBaseUrlThatHasNoScheme()
    {
        // "localhost:5082" reads as a valid absolute URI — Uri.TryCreate takes "localhost" for the
        // scheme — so the startup check has to require http or https, or a missing scheme would pass
        // validation and then fail on the first outbound call.
        using var factory = new OrderingApiFactory(fixture.ConnectionString, stockBaseUrl: "localhost:5082");

        var act = () => _ = factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Services:Stock:BaseUrl*");
    }
}
