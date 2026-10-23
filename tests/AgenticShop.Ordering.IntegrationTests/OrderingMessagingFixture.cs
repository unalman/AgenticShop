using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

// The package id is Testcontainers.RabbitMQ but the assembly and its namespace are
// Testcontainers.RabbitMq — a lower-case q. The PackageReference has to use the id and this using
// has to use the namespace, and the two disagreeing is not a typo to "fix" in either direction.
using Testcontainers.RabbitMq;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// PostgreSQL and RabbitMQ, one of each, for the two properties only a real broker can prove: that a
/// committed outbox row actually leaves the process, and that what arrives carries the identity and
/// trace context it was written with.
/// </summary>
/// <remarks>
/// <para>
/// A separate fixture rather than a second container on <see cref="OrderingApiFixture"/>, and the
/// reason is interference rather than tidiness. That fixture points the publisher at an unresolvable
/// host on purpose, so its 78 tests can assert on placement with nothing publishing behind their
/// back. Give it a real broker and <see cref="OrderingApiFixture.ResetAsync"/> starts deleting rows a
/// background loop is mid-flight on — a reset that races the publisher is not a reset.
/// </para>
/// <para>
/// Both containers are disposed in a <c>finally</c>, following the harness rule that a throw from the
/// factory must not leave a container running until Ryuk reaps it.
/// </para>
/// </remarks>
public sealed class OrderingMessagingFixture : IAsyncLifetime
{
    /// <summary>Its own database name, so a leaked connection cannot quietly reach the API suite's.</summary>
    public const string TestDatabaseName = "agenticshop_ordering_messaging_tests";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestPostgreSql.Image)
        .WithDatabase(TestDatabaseName)
        .WithUsername("agenticshop")
        .WithPassword("agenticshop_test")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder(TestRabbitMq.Image).Build();

    private OrderingApiFactory? _factory;

    /// <summary>Stand-in for Catalog, reset per test.</summary>
    public FakeCatalogClient Catalog { get; } = new();

    /// <summary>Stand-in for Stock, reset per test.</summary>
    public FakeStockClient Stock { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The broker the host publishes to and the test consumes from — the same one.</summary>
    public string BrokerUrl { get; private set; } = null!;

    public IServiceScope CreateScope() => _factory!.Services.CreateScope();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbit.StartAsync();

        BrokerUrl = _rabbit.GetConnectionString();

        _factory = new OrderingApiFactory(
            _postgres.GetConnectionString(),
            services =>
            {
                services.RemoveAll<ICatalogClient>();
                services.AddSingleton<ICatalogClient>(Catalog);
                services.RemoveAll<IStockClient>();
                services.AddSingleton<IStockClient>(Stock);
            },
            brokerUrl: BrokerUrl);

        Client = _factory.CreateClient();
    }

    /// <summary>
    /// Empties every table the host writes. The outbox is included, and it matters more here than in
    /// the API fixture: a row left behind would be republished into the queue the next test consumes
    /// from, and the failure would look like a duplicated message rather than a missed cleanup.
    /// </summary>
    public async Task ResetAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        await db.IdempotencyKeys.ExecuteDeleteAsync();
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.OrderLines.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();

        Catalog.Reset();
        Stock.Reset();
    }

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
            await _rabbit.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }
}
