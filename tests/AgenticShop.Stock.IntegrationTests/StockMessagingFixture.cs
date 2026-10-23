using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using AgenticShop.Stock.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// See the note on Ordering's messaging fixture: the package id is Testcontainers.RabbitMQ but the
// assembly and namespace are Testcontainers.RabbitMq.
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// PostgreSQL and RabbitMQ, one of each, running Stock with the consumer and publisher registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>This fixture registers two hosted services that <c>Program.cs</c> does not.</b> That is the whole
/// point of the increment: the consumer is proven against a real broker while the synchronous reserve
/// path stays the only one live in production, so nothing can double-hold. Registering them here rather
/// than in <c>Program.cs</c> means the code under test is the code that will ship, and the only
/// difference is who asked for it.
/// </para>
/// <para>
/// A separate fixture from <see cref="StockApiFixture"/> rather than extra containers on it, for the
/// reason Ordering's equivalent gives: the API fixture must not have a background loop mutating
/// <c>stock_items</c> while its tests assert on counters, and its <c>ResetDatabaseAsync</c> deleting
/// rows mid-flight would turn a clean reset into a race.
/// </para>
/// </remarks>
public sealed class StockMessagingFixture : IAsyncLifetime
{
    /// <summary>Its own database name, so a leaked connection cannot quietly reach the API suite's.</summary>
    public const string TestDatabaseName = "agenticshop_stock_messaging_tests";

    /// <summary>
    /// Bound to <c>stock.#</c>, so both outcome event types arrive on one queue and a test chooses what
    /// to expect rather than the fixture choosing for it.
    /// </summary>
    public const string OutcomeQueueName = "stock.integration-tests.outcomes";

    private static readonly TimeSpan BrokerTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestPostgreSql.Image)
        .WithDatabase(TestDatabaseName)
        .WithUsername("agenticshop")
        .WithPassword("agenticshop_test")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder(TestRabbitMq.Image).Build();

    private StockApiFactory? _factory;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The broker the host consumes from and publishes to, and the one tests use.</summary>
    public string BrokerUrl { get; private set; } = null!;

    /// <summary>
    /// Exposed so a test can build a second host against the same database with different
    /// configuration — which is how the publisher's broker-outage behaviour is exercised without
    /// pointing the whole collection at an unreachable broker.
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public IServiceScope CreateScope() => _factory!.Services.CreateScope();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbit.StartAsync();

        BrokerUrl = _rabbit.GetConnectionString();
        ConnectionString = _postgres.GetConnectionString();

        _factory = new StockApiFactory(
            ConnectionString,
            services =>
            {
                services.AddHostedService<OrderPlacedConsumer>();
                services.AddHostedService<OutboxPublisher>();
            },
            brokerUrl: BrokerUrl);

        // Creating the client starts the host, and with it both hosted services.
        Client = _factory.CreateClient();

        await DeclareTopologyAsync();
    }

    /// <summary>
    /// Declares the exchanges, Stock's work queue and the outcome queue, with exactly the parameters
    /// <see cref="OrderPlacedConsumer"/> uses.
    /// </summary>
    /// <remarks>
    /// Declared here as well as in the consumer for two reasons. It removes a startup race — a message
    /// published before the consumer connects is queued rather than discarded, because the binding
    /// already exists. And it is a check: AMQP answers a redeclaration with different parameters as
    /// <c>PRECONDITION_FAILED</c> and closes the channel, so a consumer that drifted from durable, or
    /// from topic, fails here rather than passing silently.
    /// </remarks>
    private async Task DeclareTopologyAsync() => await WithChannelAsync(async channel =>
    {
        foreach (var exchange in new[]
                 {
                     StockEventTopology.OrdersExchange,
                     StockEventTopology.StockExchange
                 })
        {
            await channel.ExchangeDeclareAsync(
                exchange: exchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null);
        }

        await channel.QueueDeclareAsync(
            queue: StockEventTopology.OrderPlacedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        await channel.QueueBindAsync(
            queue: StockEventTopology.OrderPlacedQueue,
            exchange: StockEventTopology.OrdersExchange,
            routingKey: StockEventTopology.OrderPlacedRoutingKey,
            arguments: null);

        await channel.QueueDeclareAsync(
            queue: OutcomeQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        await channel.QueueBindAsync(
            queue: OutcomeQueueName,
            exchange: StockEventTopology.StockExchange,
            routingKey: "stock.#",
            arguments: null);
    });

    /// <summary>
    /// Empties every table the host writes, and the two queues.
    /// </summary>
    /// <remarks>
    /// The queues matter as much as the tables: a leftover outcome event would be consumed by the next
    /// test and read as its own, which looks like a duplicated message rather than a missed cleanup.
    /// </remarks>
    public async Task ResetAsync()
    {
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

            // Reservations first: the foreign key to stock_items is RESTRICT.
            await db.StockReservations.ExecuteDeleteAsync();
            await db.StockItems.ExecuteDeleteAsync();
            await db.InboxMessages.ExecuteDeleteAsync();
            await db.OutboxMessages.ExecuteDeleteAsync();
        }

        await WithChannelAsync(async channel =>
        {
            await channel.QueuePurgeAsync(StockEventTopology.OrderPlacedQueue);
            await channel.QueuePurgeAsync(OutcomeQueueName);
        });
    }

    /// <summary>
    /// Publishes an <see cref="OrderPlaced"/> as Ordering's outbox would, including the AMQP properties
    /// it sets. Deliberately not a shortcut around the real producer's shape: a consumer that only ever
    /// saw messages built by its own test would not notice a producer change.
    /// </summary>
    public async Task PublishOrderPlacedAsync(
        Guid messageId,
        Guid orderId,
        string orderNumber,
        IReadOnlyList<OrderPlacedLine> lines,
        string? traceParent = null)
    {
        var payload = new OrderPlaced(
            messageId,
            orderId,
            orderNumber,
            "USD",
            DateTimeOffset.UtcNow,
            lines);

        var body = JsonSerializer.Serialize(payload, StockMessageJson.Options);

        await WithChannelAsync(async channel =>
        {
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
                exchange: StockEventTopology.OrdersExchange,
                routingKey: StockEventTopology.OrderPlacedRoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(body));
        });
    }

    /// <summary>
    /// Waits for one outcome event and returns it with the routing key that carried it.
    /// </summary>
    public async Task<(string RoutingKey, string Body, string? MessageId, string? TraceParent)>
        ConsumeOutcomeAsync()
    {
        var delivered = new TaskCompletionSource<(string, string, string?, string?)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await WithChannelAsync(async channel =>
        {
            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += (_, args) =>
            {
                // The body memory belongs to the library and is only valid inside the callback, so it
                // is copied before the task completes.
                var headers = args.BasicProperties.Headers;

                string? traceParent = null;

                if (headers is not null
                    && headers.TryGetValue("traceparent", out var value)
                    && value is not null)
                {
                    traceParent = value is byte[] bytes
                        ? Encoding.UTF8.GetString(bytes)
                        : value.ToString();
                }

                delivered.TrySetResult((
                    args.RoutingKey,
                    Encoding.UTF8.GetString(args.Body.ToArray()),
                    args.BasicProperties.MessageId,
                    traceParent));

                return Task.CompletedTask;
            };

            await channel.BasicConsumeAsync(queue: OutcomeQueueName, autoAck: true, consumer: consumer);

            using var cts = new CancellationTokenSource(BrokerTimeout);
            await using var registration = cts.Token.Register(() => delivered.TrySetCanceled(cts.Token));

            await delivered.Task.WaitAsync(cts.Token);
        });

        return await delivered.Task;
    }

    public async Task<List<StockReservation>> ReadReservationsAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        return await db.StockReservations.AsNoTracking().ToListAsync();
    }

    public async Task<List<InboxMessage>> ReadInboxAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        return await db.InboxMessages.AsNoTracking().ToListAsync();
    }

    public async Task<List<OutboxMessage>> ReadOutboxAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        return await db.OutboxMessages.AsNoTracking().ToListAsync();
    }

    public async Task<StockItem?> ReadItemAsync(Guid productId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        return await db.StockItems.AsNoTracking().FirstOrDefaultAsync(item => item.ProductId == productId);
    }

    /// <summary>Provisions stock through the real endpoint, so the counter starts from a real write.</summary>
    public async Task SeedStockAsync(Guid productId, int quantityOnHand)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/stock",
            new AgenticShop.Stock.Contracts.SetStockRequest(productId, quantityOnHand));

        response.EnsureSuccessStatusCode();
    }

    private async Task WithChannelAsync(Func<IChannel, Task> action)
    {
        var factory = new ConnectionFactory { Uri = new Uri(BrokerUrl) };

        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await action(channel);
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
