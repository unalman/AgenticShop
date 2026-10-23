using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AgenticShop.Ordering.Messaging;

/// <summary>
/// Consumes Stock's outcomes and settles the order they answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not registered in <c>Program.cs</c> yet</b>, for the same reason Stock's consumer is not: the
/// synchronous reserve path still decides every order's status, and two writers of that status would
/// contradict each other. The increment that registers this one also deletes the synchronous path, so
/// there is no state in between where both are live.
/// </para>
/// <para>
/// <b>The transition is binary, and that is a consequence of Stock's design rather than a
/// simplification here.</b> Stock holds every line of an order or none of them, so an outcome is either
/// <c>StockCommitted</c> or <c>StockReservationRejected</c> and never a mixture. That is why O14's
/// confirm-phase policy table — which existed to classify partial outcomes, including the
/// "some confirmed and cannot be undone" case that produced <c>PartiallyConfirmed</c> — has no
/// equivalent on this path. <c>PartiallyConfirmed</c> stays in the vocabulary and in the CHECK
/// constraint, because rows written by the synchronous path carry it, but the event path never
/// produces one.
/// </para>
/// <para>
/// <b>An order whose outcome never arrives stays <c>Pending</c> forever.</b> That is accepted, not
/// overlooked: resolving it needs a timeout or a poller, and this repository has no background worker
/// other than the two message pumps — the reservation-expiry worker is Phase 3. Recorded in
/// <c>../../../docs/KNOWN-ISSUES.md</c>.
/// </para>
/// </remarks>
public sealed class StockOutcomeConsumer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StockOutcomeConsumer> logger) : BackgroundService
{
    private const string ActivityName = "ordering.stock-outcome.consume";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConnection? connection = null;
        IChannel? channel = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    (connection, channel) = await OpenChannelAsync(stoppingToken);

                    var consumer = new AsyncEventingBasicConsumer(channel);

                    consumer.ReceivedAsync += async (_, args) =>
                    {
                        // With prefetch 1 an exception here leaves the delivery unacknowledged and the
                        // consumer stalls forever holding it, which turns one bad message into an
                        // outage.
                        await HandleAsync(channel, args, stoppingToken);
                    };

                    await channel.BasicConsumeAsync(
                        queue: StockEventTopology.OutcomeQueue,
                        autoAck: false,
                        consumer: consumer,
                        cancellationToken: stoppingToken);

                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Stock outcome consumer lost its connection; unacknowledged deliveries will be redelivered. {ExceptionType}: {Message}",
                        exception.GetType().Name,
                        exception.Message);

                    await DisposeAsync(connection, channel);
                    connection = null;
                    channel = null;

                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            await DisposeAsync(connection, channel);
        }
    }

    /// <summary>
    /// Settles one delivery. Every path ends in an ack or a nack; none throws.
    /// </summary>
    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        var body = Encoding.UTF8.GetString(args.Body.ToArray());
        var traceParent = ReadTraceParent(args);

        // Routing decides the payload type, so a message on an unbound key cannot reach here — and if
        // one somehow does, treating it as malformed is right, because there is nothing to settle it
        // with.
        if (!TryReadOutcome(args.RoutingKey, body, out var messageId, out var orderId, out var reserved))
        {
            await AckAsync(channel, args.DeliveryTag, stoppingToken);
            return;
        }

        using var activity = StartActivity(traceParent);

        try
        {
            await SettleAsync(messageId, args.RoutingKey, orderId, reserved, traceParent, body, args, channel, stoppingToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two deliveries racing on one order row. Bounded by the broker's own redelivered flag,
            // because a nack that requeues forever is a spin loop.
            if (args.Redelivered)
            {
                logger.LogError(
                    "Outcome {MessageId} for order {OrderId} lost the concurrency race twice; dropping it. Body: {Body}",
                    messageId,
                    orderId,
                    body);

                await AckAsync(channel, args.DeliveryTag, stoppingToken);
            }
            else
            {
                logger.LogWarning(
                    "Outcome {MessageId} for order {OrderId} lost the concurrency race; requeueing once",
                    messageId,
                    orderId);

                await NackAsync(channel, args.DeliveryTag, requeue: true, stoppingToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Outcome {MessageId} for order {OrderId} failed in a way redelivery cannot fix; dropping it. Body: {Body}",
                messageId,
                orderId,
                body);

            await AckAsync(channel, args.DeliveryTag, stoppingToken);
        }
    }

    private async Task SettleAsync(
        Guid messageId,
        string routingKey,
        Guid orderId,
        bool reserved,
        string? traceParent,
        string body,
        BasicDeliverEventArgs args,
        IChannel channel,
        CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, stoppingToken);

        if (order is null)
        {
            // The order row and the OrderPlaced outbox row commit in one transaction, so an outcome
            // for an order that does not exist means one of the two writers is broken. Nothing here
            // can repair it, and redelivery cannot make the row appear.
            logger.LogError(
                "Outcome {MessageId} arrived for order {OrderId}, which does not exist; dropping it. Body: {Body}",
                messageId,
                orderId,
                body);

            await AckAsync(channel, args.DeliveryTag, stoppingToken);
            return;
        }

        if (order.IsTerminal)
        {
            // Already settled — and this is the one place the inbox earns its keep. The same message
            // id means an ordinary redelivery; a different one means Stock published two outcomes for
            // one OrderPlaced, which is a producer bug and not a fact to log at Information.
            var alreadyProcessed = await db.InboxMessages
                .AnyAsync(m => m.MessageId == messageId, stoppingToken);

            if (alreadyProcessed)
            {
                logger.LogInformation(
                    "Outcome {MessageId} for order {OrderId} was already applied; acknowledging the redelivery",
                    messageId,
                    orderId);
            }
            else
            {
                logger.LogError(
                    "Outcome {MessageId} ({RoutingKey}) arrived for order {OrderId}, which is already " +
                    "{Status} and was not settled by this message. Two outcomes for one order is a " +
                    "producer bug; dropping it. Body: {Body}",
                    messageId,
                    routingKey,
                    orderId,
                    order.Status,
                    body);
            }

            await AckAsync(channel, args.DeliveryTag, stoppingToken);
            return;
        }

        if (reserved)
        {
            order.Confirm();
        }
        else
        {
            order.Fail();
        }

        // The transition and the inbox row commit together, so "settled" and "this message was the
        // one that settled it" cannot disagree — which is what the terminal branch above relies on.
        db.InboxMessages.Add(InboxMessage.Create(messageId, routingKey, DateTimeOffset.UtcNow));

        try
        {
            await db.SaveChangesAsync(stoppingToken);

            logger.LogInformation(
                "Order {OrderId} settled as {Status} by {RoutingKey} {MessageId}",
                orderId,
                order.Status,
                routingKey,
                messageId);

            await AckAsync(channel, args.DeliveryTag, stoppingToken);
        }
        catch (DbUpdateException exception)
            when (ConstraintName(exception) == InboxMessageConfiguration.PrimaryKeyName)
        {
            // A concurrent delivery of the same message won the race. Its transaction carried the
            // transition too, so the order is settled exactly once and nothing is missing.
            logger.LogInformation(
                "Outcome {MessageId} for order {OrderId} was applied concurrently; acknowledging",
                messageId,
                orderId);

            await AckAsync(channel, args.DeliveryTag, stoppingToken);
        }
    }

    /// <summary>
    /// Reads the delivery into the one distinction the transition needs: which order, and whether it
    /// was held.
    /// </summary>
    /// <remarks>
    /// The message id comes from the AMQP property rather than the body, because that is the identity a
    /// broker redelivers unchanged. The routing key decides the payload type, so the two outcome
    /// records are not guessed at from whichever fields happen to be present.
    /// </remarks>
    private bool TryReadOutcome(
        string routingKey,
        string body,
        out Guid messageId,
        out Guid orderId,
        out bool reserved)
    {
        messageId = Guid.Empty;
        orderId = Guid.Empty;
        reserved = false;

        var isReserved = routingKey == StockEventTopology.CommittedRoutingKey;
        var isRejected = routingKey == StockEventTopology.ReservationRejectedRoutingKey;

        if (!isReserved && !isRejected)
        {
            logger.LogError(
                "Delivery arrived on unrecognised routing key '{RoutingKey}'; dropping it. Body: {Body}",
                routingKey,
                body);

            return false;
        }

        var propertyMessageId = ReadMessageId(body, routingKey);

        if (propertyMessageId is null)
        {
            return false;
        }

        messageId = propertyMessageId.Value;

        try
        {
            if (isReserved)
            {
                var payload = JsonSerializer.Deserialize<StockCommitted>(body, OrderPlaced.JsonOptions);

                if (payload is null)
                {
                    return Fail(routingKey, messageId, body, "deserialised to nothing");
                }

                orderId = payload.OrderId;
                reserved = true;

                return true;
            }

            var rejection = JsonSerializer.Deserialize<StockReservationRejected>(body, OrderPlaced.JsonOptions);

            if (rejection is null)
            {
                return Fail(routingKey, messageId, body, "deserialised to nothing");
            }

            orderId = rejection.OrderId;

            logger.LogInformation(
                "Stock refused order {OrderId}: {Reason} on product {FailedProductId}",
                rejection.OrderId,
                rejection.Reason,
                rejection.FailedProductId);

            return true;
        }
        catch (JsonException exception)
        {
            return Fail(routingKey, messageId, body, $"is not valid JSON ({exception.Message})");
        }
    }

    /// <summary>
    /// The message id, read from the payload because that is where both outcome records carry it.
    /// </summary>
    /// <remarks>
    /// Stock sets the AMQP property and the payload field from one value, so either would do; reading
    /// the payload keeps one deserialisation path rather than two sources that could be compared and
    /// then disagreed about.
    /// </remarks>
    private Guid? ReadMessageId(string body, string routingKey)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("messageId", out var property)
                && property.TryGetGuid(out var messageId)
                && messageId != Guid.Empty)
            {
                return messageId;
            }
        }
        catch (JsonException)
        {
            // Falls through to the failure below, which logs the body.
        }

        Fail(routingKey, null, body, "carries no usable messageId, so it cannot be deduplicated");

        return null;
    }

    private bool Fail(string routingKey, Guid? messageId, string body, string because)
    {
        // Deterministic: redelivery cannot make a malformed message parseable, and there is no
        // dead-letter queue to put it in. The body in this log line is the only copy that survives.
        logger.LogError(
            "Outcome on {RoutingKey} (message {MessageId}) {Because}; dropping it. Body: {Body}",
            routingKey,
            messageId?.ToString() ?? "<none>",
            because,
            body);

        return false;
    }

    /// <summary>
    /// Declares Stock's exchange, Ordering's own queue, and the two bindings.
    /// </summary>
    /// <remarks>
    /// The exchange is declared here even though Stock owns it, because declaration is idempotent and a
    /// consumer that starts before its producer must not fail on a binding to an exchange that does not
    /// exist yet. The queue is durable: RabbitMQ 4 rejects a transient non-exclusive one outright
    /// (<c>code=541</c>), and exclusive is not available to a consumer that must survive a restart.
    /// </remarks>
    private async Task<(IConnection Connection, IChannel Channel)> OpenChannelAsync(
        CancellationToken stoppingToken)
    {
        var url = OutboxPublisher.RequireBrokerUrl(configuration);

        var factory = new ConnectionFactory { Uri = new Uri(url) };

        var connection = await factory.CreateConnectionAsync(stoppingToken);

        try
        {
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false,
                    consumerDispatchConcurrency: 1),
                stoppingToken);

            try
            {
                await channel.ExchangeDeclareAsync(
                    exchange: StockEventTopology.StockExchange,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    noWait: false,
                    cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync(
                    queue: StockEventTopology.OutcomeQueue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    noWait: false,
                    cancellationToken: stoppingToken);

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
                        arguments: null,
                        noWait: false,
                        cancellationToken: stoppingToken);
                }

                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: StockEventTopology.PrefetchCount,
                    global: false,
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

    /// <summary>
    /// The inbound <c>traceparent</c>, in whichever of the two shapes a broker may deliver a header
    /// value. That encoding is the broker's choice, so this is a boundary and both are handled.
    /// </summary>
    private static string? ReadTraceParent(BasicDeliverEventArgs args)
    {
        if (args.BasicProperties.Headers is not { } headers
            || !headers.TryGetValue("traceparent", out var value)
            || value is null)
        {
            return null;
        }

        return value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value.ToString();
    }

    /// <summary>
    /// Makes the inbound trace ambient, so every log line this delivery writes carries Stock's trace
    /// id — and therefore the trace id of the placement that started the whole chain.
    /// </summary>
    /// <remarks>
    /// An <see cref="Activity"/> is created directly rather than through an <c>ActivitySource</c>,
    /// because <c>StartActivity</c> returns null when nothing is listening and this source is not
    /// registered with the tracer provider — and <c>AgenticShop.Shared/Tracing</c> configures
    /// instrumentation for all three services, so a service-specific source name does not belong there.
    /// Correlation therefore works while export does not; recorded in
    /// <c>../../../docs/KNOWN-ISSUES.md</c>.
    /// </remarks>
    private static Activity? StartActivity(string? traceParent)
    {
        var activity = new Activity(ActivityName);

        if (traceParent is not null)
        {
            activity.SetParentId(traceParent);
        }

        activity.Start();

        return activity;
    }

    private static string? ConstraintName(DbUpdateException exception)
        => (exception.InnerException as PostgresException)?.ConstraintName;

    private static async Task AckAsync(IChannel channel, ulong deliveryTag, CancellationToken stoppingToken)
        => await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: stoppingToken);

    private static async Task NackAsync(
        IChannel channel,
        ulong deliveryTag,
        bool requeue,
        CancellationToken stoppingToken)
        => await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: requeue, cancellationToken: stoppingToken);

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
}
