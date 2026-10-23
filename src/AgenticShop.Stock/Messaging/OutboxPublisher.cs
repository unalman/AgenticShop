using System.Text;
using AgenticShop.Stock.Data;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace AgenticShop.Stock.Messaging;

/// <summary>
/// Drains the outbox: publishes every committed-but-unpublished row and marks it published once the
/// broker has accepted it.
/// </summary>
/// <remarks>
/// <para>
/// A copy of Ordering's publisher rather than a shared one, on the project owner's explicit call:
/// extracting it into <c>AgenticShop.Shared</c> would widen what that assembly is allowed to hold, and
/// freezing its shape before a second consumer existed is the mistake its own deferral entry names.
/// What keeps two copies honest is <c>OutboxPublisherSafetyTests</c>: each service's suite asserts the
/// same safety properties against its own copy, because the assembly boundary forbids comparing them
/// directly. Ordering's suite currently proves delivery but not these two properties, which is
/// recorded as a gap in <c>docs/KNOWN-ISSUES.md</c> rather than left implied.
/// </para>
/// <para>
/// The three rules that make a hosted service safe here, restated rather than cross-referenced so this
/// file can be read on its own:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Nothing may escape <see cref="ExecuteAsync"/>.</b> An unhandled exception in a hosted service
/// stops the host, so a broker outage would take down an API that can still serve every read it serves
/// and still write outbox rows.
/// </item>
/// <item>
/// <b>There is no ambient <c>Activity</c>.</b> The traceparent travels in a column on the row and is
/// put back onto the message as a header.
/// </item>
/// <item>
/// <b>The broker URL is read per connection attempt, never captured at registration.</b> Ordering's
/// readiness probe once froze its target <c>Uri</c> at registration, so a test host's override never
/// applied — and the test passed because nothing was listening.
/// </item>
/// </list>
/// </remarks>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    /// <summary>How many rows one poll takes. Bounds the work a single iteration can hold.</summary>
    internal const int BatchSize = 100;

    /// <summary>Pause when the outbox is empty. A tight poll would burn a query per few milliseconds for nothing.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    /// <summary>Pause after a failure. Fixed rather than exponential: the rows are not going anywhere, and a backoff long enough to matter would delay recovery from a broker restart.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConnection? connection = null;
        IChannel? channel = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // How long to wait before the next poll. Three cases, and they are not the same
                // number: nothing to do, more to do, and something went wrong.
                var delay = IdleDelay;

                try
                {
                    if (channel is null)
                    {
                        (connection, channel) = await OpenChannelAsync(stoppingToken);
                    }

                    if (await PublishPendingAsync(channel, stoppingToken) > 0)
                    {
                        // A full batch means there are probably more rows behind it, so go straight
                        // round instead of idling for a second between them.
                        delay = TimeSpan.Zero;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // Warning without the exception object: a broker that is down is an operational
                    // condition the outbox exists to survive, not a defect, and the unpublished rows
                    // say what is pending. Severity follows the same split the exception handler uses.
                    logger.LogWarning(
                        "Outbox publish failed; unpublished rows stay queued and will be retried. {ExceptionType}: {Message}",
                        exception.GetType().Name,
                        exception.Message);

                    // Discard both: an AMQP channel is closed by the broker after most protocol-level
                    // failures, and publishing on a closed one throws forever.
                    await DisposeAsync(connection, channel);
                    connection = null;
                    channel = null;

                    delay = RetryDelay;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            await DisposeAsync(connection, channel);
        }
    }

    /// <summary>
    /// Publishes one batch. Returns how many rows were handed to the broker.
    /// </summary>
    /// <remarks>
    /// Each row is saved on its own, immediately after its publish is acknowledged. Saving the batch
    /// once at the end would be fewer round trips but would widen the duplicate window from one message
    /// to the whole batch. Batches are normally one or two rows, so the round trips cost nothing.
    /// </remarks>
    private async Task<int> PublishPendingAsync(IChannel channel, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

        var pending = await db.OutboxMessages
            .Where(message => message.PublishedAtUtc == null)
            .OrderBy(message => message.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(stoppingToken);

        foreach (var message in pending)
        {
            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(),
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent
            };

            if (message.TraceParent is not null)
            {
                properties.Headers = new Dictionary<string, object?>
                {
                    ["traceparent"] = message.TraceParent
                };
            }

            var body = Encoding.UTF8.GetBytes(message.PayloadJson);

            // mandatory is false, and in this increment it has to be — for the mirror image of the
            // reason it is false in Ordering. Nothing binds a queue to agenticshop.stock until
            // Ordering's outcome consumer arrives in the next increment, so a mandatory publish would
            // come back as basic.return, surface as a PublishException, and leave the row unpublished
            // to be retried forever. Messages published now are accepted and discarded.
            //
            // That makes two copies of this flag whose correct value depends on a later increment,
            // which is exactly how one of them gets forgotten. Both are recorded in
            // docs/KNOWN-ISSUES.md, and the consumer that binds a queue is the one that should flip
            // the producer's flag.
            await channel.BasicPublishAsync(
                exchange: message.Exchange,
                routingKey: message.RoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: stoppingToken);

            message.MarkPublished(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(stoppingToken);
        }

        return pending.Count;
    }

    /// <summary>
    /// Opens a connection and a channel, and declares Stock's exchange.
    /// </summary>
    /// <remarks>
    /// Publisher confirms are on, which is what makes marking a row published mean something. Without
    /// them a successful <c>BasicPublishAsync</c> only proves the bytes reached the socket, so a broker
    /// that dies before persisting would leave the row marked published and the outcome event gone —
    /// and a saga waiting on that event would wait forever.
    /// </remarks>
    private async Task<(IConnection Connection, IChannel Channel)> OpenChannelAsync(
        CancellationToken stoppingToken)
    {
        var url = RequireBrokerUrl(configuration);

        var factory = new ConnectionFactory { Uri = new Uri(url) };

        var connection = await factory.CreateConnectionAsync(stoppingToken);

        try
        {
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                stoppingToken);

            try
            {
                // Stock's own exchange, for the outcomes it publishes. Durable, so a broker restart
                // does not discard events whose rows are already marked published.
                await channel.ExchangeDeclareAsync(
                    exchange: StockEventTopology.StockExchange,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    noWait: false,
                    cancellationToken: stoppingToken);

                return (connection, channel);
            }
            catch
            {
                await channel.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask DisposeAsync(IConnection? connection, IChannel? channel)
    {
        if (channel is not null)
        {
            await channel.DisposeAsync();
        }

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads and validates the broker URL. Public so <c>Program.cs</c> can fail at startup with the
    /// same message rather than on the publisher's first attempt.
    /// </summary>
    internal static string RequireBrokerUrl(IConfiguration configuration)
    {
        const string key = BrokerUrlKey;
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Broker URL '{key}' is required. Set {key} in configuration or " +
                $"{key.Replace(':', '_')} as an environment variable.");
        }

        // The scheme check is not redundant: Uri accepts "localhost:5672" by reading "localhost" as
        // the scheme, so an absolute-URI test alone passes a value that cannot reach a broker.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "amqp" && uri.Scheme != "amqps"))
        {
            throw new InvalidOperationException(
                $"Broker URL '{key}' must be an absolute amqp or amqps URL. Found '{value}'.");
        }

        return value;
    }

    internal const string BrokerUrlKey = "RabbitMq:Url";
}
