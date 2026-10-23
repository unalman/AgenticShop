using System.Text;
using System.Text.Json;
using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using AgenticShop.Ordering.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;
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

                // The one hosted service Program.cs deliberately does not register yet. Everything
                // else the host runs — including the outbox publisher — comes from Program.cs, so
                // the consumer under test is wired exactly as it will be in production and only the
                // decision to start it is the test's.
                services.AddHostedService<StockOutcomeConsumer>();
            },
            brokerUrl: BrokerUrl);

        Client = _factory.CreateClient();

        await DeclareOutcomeTopologyAsync();
    }

    /// <summary>
    /// Declares Stock's exchange, Ordering's outcome queue and the two bindings, with exactly the
    /// parameters <see cref="StockOutcomeConsumer"/> uses.
    /// </summary>
    /// <remarks>
    /// Declared here as well as in the consumer for two reasons. It removes a startup race, so an
    /// outcome published before the consumer connects is queued rather than discarded. And it is a
    /// check: AMQP answers a redeclaration with different parameters as <c>PRECONDITION_FAILED</c> and
    /// closes the channel, so a consumer that drifted from durable, or from topic, fails here rather
    /// than passing silently.
    /// </remarks>
    private async Task DeclareOutcomeTopologyAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(BrokerUrl) };

        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.ExchangeDeclareAsync(
            exchange: StockEventTopology.StockExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null);

        await channel.QueueDeclareAsync(
            queue: StockEventTopology.OutcomeQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        foreach (var routingKey in new[]
                 {
                     StockEventTopology.CommittedRoutingKey,
                     StockEventTopology.ReservationRejectedRoutingKey
                 })
        {
            await channel.QueueBindAsync(
                queue: StockEventTopology.OutcomeQueue,
                exchange: StockEventTopology.StockExchange,
                routingKey: routingKey,
                arguments: null);
        }
    }

    /// <summary>
    /// Empties every table the host writes. The outbox is included, and it matters more here than in
    /// the API fixture: a row left behind would be republished into the queue the next test consumes
    /// from, and the failure would look like a duplicated message rather than a missed cleanup.
    /// </summary>
    public async Task ResetAsync()
    {
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

            await db.IdempotencyKeys.ExecuteDeleteAsync();
            await db.InboxMessages.ExecuteDeleteAsync();
            await db.OutboxMessages.ExecuteDeleteAsync();
            await db.OrderLines.ExecuteDeleteAsync();
            await db.Orders.ExecuteDeleteAsync();
        }

        // The queue matters as much as the tables: an outcome left behind would be consumed by the
        // next test and read as its own, which looks like a duplicated message rather than a missed
        // cleanup.
        var factory = new ConnectionFactory { Uri = new Uri(BrokerUrl) };

        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueuePurgeAsync(StockEventTopology.OutcomeQueue);

        Catalog.Reset();
        Stock.Reset();
    }

    /// <summary>
    /// Writes a <c>Pending</c> order straight to the database, bypassing the endpoint.
    /// </summary>
    /// <remarks>
    /// Necessary rather than convenient. <c>POST /orders</c> still runs the synchronous path, which
    /// settles an order before it saves one — <c>OrderPlacer.WriteAsync</c> throws on
    /// <see cref="OrderStatus.Pending"/> — so the API cannot produce the state this consumer acts on.
    /// Seeding through it would mean testing a consumer against a state production cannot reach. This
    /// seeds the state it will actually see once the flip lands, and the order goes through
    /// <see cref="Order.Create"/> so the domain's own guards still run.
    /// </remarks>
    public async Task<Order> SeedPendingOrderAsync(
        Guid productId,
        int quantity,
        decimal unitPrice = 10.00m)
    {
        var orderId = Guid.NewGuid();

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = Order.Create(
            orderId,
            "USD",
            [OrderLine.Create(orderId, productId, "Seeded Widget", unitPrice, quantity)]);

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return order;
    }

    /// <summary>Publishes an outcome as Stock's outbox would, properties and header included.</summary>
    public Task PublishStockCommittedAsync(
        Guid messageId,
        Guid orderId,
        IReadOnlyList<StockCommittedLine> lines,
        string? traceParent = null)
        => PublishOutcomeAsync(
            StockEventTopology.CommittedRoutingKey,
            new StockCommitted(messageId, orderId, DateTimeOffset.UtcNow, lines),
            messageId,
            traceParent);

    public Task PublishStockReservationRejectedAsync(
        Guid messageId,
        Guid orderId,
        RejectionReason reason,
        Guid failedProductId,
        string? traceParent = null)
        => PublishOutcomeAsync(
            StockEventTopology.ReservationRejectedRoutingKey,
            new StockReservationRejected(
                messageId, orderId, reason, failedProductId, DateTimeOffset.UtcNow),
            messageId,
            traceParent);

    private async Task PublishOutcomeAsync<TOutcome>(
        string routingKey,
        TOutcome payload,
        Guid messageId,
        string? traceParent)
    {
        var factory = new ConnectionFactory { Uri = new Uri(BrokerUrl) };

        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var properties = new BasicProperties
        {
            MessageId = messageId.ToString(),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };

        if (traceParent is not null)
        {
            properties.Headers = new Dictionary<string, object?>
            {
                ["traceparent"] = traceParent
            };
        }

        await channel.BasicPublishAsync(
            exchange: StockEventTopology.StockExchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(payload, OrderPlaced.JsonOptions)));
    }

    public async Task<Order?> ReadOrderAsync(Guid orderId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.Orders
            .AsNoTracking()
            .Include(order => order.Lines)
            .FirstOrDefaultAsync(order => order.Id == orderId);
    }

    public async Task<List<InboxMessage>> ReadInboxAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.InboxMessages.AsNoTracking().ToListAsync();
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
