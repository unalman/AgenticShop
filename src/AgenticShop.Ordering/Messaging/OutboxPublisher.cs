using System.Text;
using AgenticShop.Ordering.Data;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace AgenticShop.Ordering.Messaging;

/// <summary>
/// Drains the outbox: publishes every committed-but-unpublished row and marks it published once the
/// broker has accepted it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the repository's first <see cref="BackgroundService"/>, and there was no precedent to
/// copy.</b> Three consequences follow that a reader should not have to rediscover:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Nothing may escape <see cref="ExecuteAsync"/>.</b> An unhandled exception in a hosted service
/// stops the host by default, so a broker outage would take down an API that is still perfectly able
/// to serve reads and to write outbox rows — the exact conflation the two health endpoints exist to
/// prevent. Every failure is caught, logged and waited out.
/// </item>
/// <item>
/// <b>There is no ambient <c>Activity</c> here.</b> The correlation id is the request's W3C trace id,
/// and a background loop has no request. That is why the traceparent travels in a column on the row
/// and is put back onto the message as a header, instead of being read off <c>Activity.Current</c>.
/// </item>
/// <item>
/// <b>The broker URL is read per connection attempt, never captured at registration.</b> Ordering's
/// readiness probe once built its target <c>Uri</c> at registration and froze
/// <c>appsettings.Development.json</c>, so a test host's override never applied; the test that should
/// have caught it passed because nothing was listening. See <c>docs/ARCHITECTURE.md</c> §5.4.
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
                    // Warning without the exception object for the ordinary case — a broker that is
                    // down is an operational condition the outbox exists to survive, not a defect,
                    // and the rows say what is pending. Severity follows the same split the
                    // exception handler uses: an expected condition is not an Error.
                    logger.LogWarning(
                        "Outbox publish failed; unpublished rows stay queued and will be retried. {ExceptionType}: {Message}",
                        exception.GetType().Name,
                        exception.Message);

                    // Discard the channel and connection: an AMQP channel is closed by the broker
                    // after most protocol-level failures, and publishing on a closed one throws
                    // forever. Reopening on the next iteration is the recovery.
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
    /// once at the end would be fewer round trips but would widen the duplicate window from one
    /// message to the whole batch: a crash after publishing would leave every row unpublished and
    /// republish all of them. Batches are normally one or two rows, so the round trips cost nothing
    /// and the tighter window is worth them. Duplicates remain possible — a publish acknowledged by
    /// the broker and then lost before its row is saved — which is what makes delivery at-least-once
    /// and the consumer's inbox necessary rather than optional.
    /// </remarks>
    private async Task<int> PublishPendingAsync(IChannel channel, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var pending = await db.OutboxMessages
            .Where(message => message.PublishedAtUtc == null)
            .OrderBy(message => message.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(stoppingToken);

        foreach (var message in pending)
        {
            var properties = new BasicProperties
            {
                // The row id, which is stable across redelivery and is what a consumer's inbox keys
                // on. Set from the same value the payload carries, so the two cannot disagree.
                MessageId = message.Id.ToString(),
                ContentType = "application/json",
                // Persistent, so a broker restart does not discard a message the outbox believes it
                // delivered. Meaningless without the durable exchange and the durable queue, which
                // is why all three are declared rather than assumed.
                DeliveryMode = DeliveryModes.Persistent
            };

            if (message.TraceParent is not null)
            {
                // The W3C header name, and the mechanism §4.8 describes for a hop — done by hand
                // here because there is no AMQP instrumentation to do it, unlike the HttpClient
                // pipeline that propagates it for the downstream calls.
                properties.Headers = new Dictionary<string, object?>
                {
                    ["traceparent"] = message.TraceParent
                };
            }

            var body = Encoding.UTF8.GetBytes(message.PayloadJson);

            // mandatory is false, and in this increment it has to be: nothing binds a queue to the
            // exchange until Phase 2's Stock consumer arrives, so a mandatory publish would come
            // back as basic.return, surface as a PublishException, and leave the row unpublished to
            // be retried forever. Messages published now are accepted and discarded by the broker.
            // 2b binds a queue and should flip this to true, where it earns its keep by turning a
            // missing binding into a loud failure instead of a silent one.
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
    /// Opens a connection and a channel, and declares the exchange.
    /// </summary>
    /// <remarks>
    /// Publisher confirms are enabled, which is what makes marking a row published mean something.
    /// Without them a successful <c>BasicPublishAsync</c> only proves the bytes reached the socket,
    /// so a broker that dies before persisting would leave the row marked published and the message
    /// gone — the outbox would have traded a dual-write bug for a quieter version of the same bug.
    /// With them, <c>BasicPublishAsync</c> throws <c>PublishException</c> on a nack and the row stays
    /// unpublished.
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
                // Durable, so the exchange survives a broker restart. Declaring it here rather than
                // from a script is idempotent and needs no administrator privilege beyond what the
                // broker user already has; the queue and its binding belong to the consumer, so
                // Ordering stays ignorant of who is listening.
                await channel.ExchangeDeclareAsync(
                    exchange: OrderEventTopology.Exchange,
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

        // The scheme check is not redundant, for the reason RequireServiceBaseUrl gives: Uri
        // accepts "localhost:5672" by reading "localhost" as the scheme, so an absolute-URI test
        // alone passes a value that cannot reach a broker.
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
