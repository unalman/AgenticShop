using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// One real PostgreSQL container per test run, shared by every test in the collection, with the
/// downstream service faked at the interface seam.
/// </summary>
/// <remarks>
/// <para>
/// The database is real and the client is not, and that split is the point. Only PostgreSQL can
/// raise the 23505 behind a duplicate order number, enforce the CHECK constraints, apply the xmin
/// token or translate the order/lines join. Only a fake can make Catalog refuse a product, return a
/// price in a second currency, or time out mid-placement — behaviours a test has to provoke
/// deterministically, which a live host cannot do without a proxy in front of it.
/// </para>
/// <para>
/// Faking here is sanctioned rather than a shortcut: <c>docs/ROADMAP.md</c> names the typed-client
/// interface "the seam the integration tests fake". EF Core is never mocked.
/// </para>
/// <para>
/// Stock is not faked, because nothing calls it: since the flip a placement publishes
/// <c>OrderPlaced</c> and stops, so there is no synchronous Stock call to script. The fixture's
/// broker is unresolvable for the same reason — see <see cref="OrderingMessagingFixture"/> for the
/// suite that wants a real one on the other end.
/// </para>
/// </remarks>
public sealed class OrderingApiFixture : IAsyncLifetime
{
    /// <summary>
    /// Exposed so TestHostIsolationTests can prove the app really is talking to this container
    /// rather than to a developer's compose database.
    /// </summary>
    public const string TestDatabaseName = "agenticshop_ordering_tests";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestPostgreSql.Image)
        .WithDatabase(TestDatabaseName)
        .WithUsername("agenticshop")
        .WithPassword("agenticshop_test")
        .Build();

    private OrderingApiFactory? _factory;

    /// <summary>Stand-in for Catalog. Configured per test; reset by <see cref="ResetAsync"/>.</summary>
    public FakeCatalogClient Catalog { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    /// <summary>For tests that build their own host, e.g. the startup-validation one.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>
    /// Exposes the host's services so a test can open independent units of work against the same
    /// database and assert on what was actually written rather than only on the response.
    /// </summary>
    public IServiceScope CreateScope() => _factory!.Services.CreateScope();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        ConnectionString = _postgres.GetConnectionString();

        _factory = new OrderingApiFactory(ConnectionString, services =>
        {
            services.RemoveAll<ICatalogClient>();
            services.AddSingleton<ICatalogClient>(Catalog);
        });

        Client = _factory.CreateClient();
    }

    /// <summary>
    /// Tests share a database and a fake, so each one starts from empty rather than depending on
    /// execution order. Lines go first even though the foreign key cascades, so the reset does not
    /// depend on that behaviour staying.
    /// </summary>
    public async Task ResetAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        // No foreign key from order_idempotency_keys to orders — the key names an order id but
        // deliberately does not reference it, so a stranded claim can outlive the row it points at.
        // That means the delete order here is free, unlike order_lines → orders.
        await db.IdempotencyKeys.ExecuteDeleteAsync();

        // Same shape, same reason: an outbox row carries a serialised payload and no key into
        // orders, so nothing cascades and a row left behind would be republished by the next test's
        // publisher against an order that no longer exists.
        await db.OutboxMessages.ExecuteDeleteAsync();

        await db.OrderLines.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();

        Catalog.Reset();
    }

    /// <summary>
    /// Disposal is exception-safe, following Stock's fixture rather than Catalog's: a throw from the
    /// factory must not leave the container running until Ryuk reaps it.
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
