using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AgenticShop.Stock.Messaging;

/// <summary>
/// Consumes <see cref="OrderPlaced"/>, holds the stock, and publishes the outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not registered in <c>Program.cs</c> yet.</b> This increment adds the consumer and proves it
/// against a real broker, but the synchronous <c>POST /api/v1/stock/{productId}/reservations</c> path
/// is still the only one live, so Ordering's placement behaves exactly as it did. Both paths reserving
/// at once would double-hold every order. The next increment registers this, deletes the synchronous
/// reserve, and moves Ordering's emission point — in one commit, so there is no state in between where
/// both are wired.
/// </para>
/// <para>
/// <b>Everything is one transaction or nothing.</b> The inbox row, the reservations, the counter
/// mutations and the outcome event are written in a single <c>SaveChangesAsync</c>, which is what makes
/// "processed" and "answered" one fact instead of two. Splitting them reopens the hole the inbox exists
/// to close: a first attempt that was refused writes no reservation, so a redelivery has nothing to
/// collide with — and if stock arrived in between, it succeeds and publishes <c>StockReserved</c> for
/// an order already answered with <c>StockReservationRejected</c>.
/// </para>
/// <para>
/// <b>It reserves; it does not confirm.</b> The holds stay <c>Pending</c>. A <c>Confirmed</c>
/// reservation is terminal (Stock's D3) and Ordering is forbidden from releasing one (its O10), so
/// confirming here would throw away the only compensation the saga has. Confirming these holds is a
/// later increment's decision, and until then they sit <c>Pending</c>.
/// </para>
/// </remarks>
public sealed class OrderPlacedConsumer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<OrderPlacedConsumer> logger) : BackgroundService
{
    /// <summary>
    /// Named for the activity rather than for a listener. See <see cref="StartActivity"/> for why this
    /// creates an <see cref="Activity"/> directly instead of going through an <c>ActivitySource</c>.
    /// </summary>
    private const string ActivityName = "stock.order-placed.consume";

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
                        // The handler must never throw. With prefetch 1 an exception here leaves the
                        // delivery unacknowledged and the consumer stalls forever holding it, which
                        // turns one bad message into an outage.
                        await HandleAsync(channel, args, stoppingToken);
                    };

                    await channel.BasicConsumeAsync(
                        queue: StockEventTopology.OrderPlacedQueue,
                        autoAck: false,
                        consumer: consumer,
                        cancellationToken: stoppingToken);

                    // The subscription lives as long as the channel, so the loop waits here rather
                    // than returning and disposing it.
                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "OrderPlaced consumer lost its connection; unacknowledged deliveries will be redelivered. {ExceptionType}: {Message}",
                        exception.GetType().Name,
                        exception.Message);

                    await DisposeAsync(connection, channel);
                    connection = null;
                    channel = null;

                    // Unacknowledged messages go back on the queue when the connection drops, so a
                    // reconnect resumes rather than loses work. Waiting before retrying keeps a broker
                    // that is down from being hammered once per loop iteration.
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
    /// Processes one delivery and settles its acknowledgement. Every path ends in an ack or a nack;
    /// none throws.
    /// </summary>
    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        var body = Encoding.UTF8.GetString(args.Body.ToArray());

        // A malformed delivery is deterministic: redelivering it cannot make it parse. Without a
        // dead-letter queue there is nowhere to put it, so it is logged at Error with the body — the
        // only copy that will exist — and acknowledged. See docs/KNOWN-ISSUES.md: nacking without a
        // DLQ discards it anyway, and requeuing loops forever filling the log.
        if (!TryReadMessage(args, body, out var messageId, out var traceParent, out var payload))
        {
            await AckAsync(channel, args.DeliveryTag, stoppingToken);
            return;
        }

        using var activity = StartActivity(traceParent);

        try
        {
            var outcome = await ReserveAsync(messageId, traceParent, payload, stoppingToken);

            if (outcome == Outcome.Duplicate)
            {
                // Already processed, and its outcome was already published — the inbox row and the
                // outbox row committed together. Acknowledging is correct, not a lost event.
                logger.LogInformation(
                    "OrderPlaced {MessageId} was already processed; acknowledging the redelivery", messageId);
            }

            await AckAsync(channel, args.DeliveryTag, stoppingToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // An xmin conflict on stock_items: another writer moved the counter underneath this
            // decision. Transient, so it is worth one more attempt — but bounded, because a nack that
            // requeues forever is a spin loop. Redelivered is the broker's own flag, so the second
            // failure is the last one.
            if (args.Redelivered)
            {
                logger.LogError(
                    "OrderPlaced {MessageId} lost the stock_items concurrency race twice; dropping it. Body: {Body}",
                    messageId,
                    body);

                await AckAsync(channel, args.DeliveryTag, stoppingToken);
            }
            else
            {
                logger.LogWarning(
                    "OrderPlaced {MessageId} lost the stock_items concurrency race; requeueing once", messageId);

                await NackAsync(channel, args.DeliveryTag, requeue: true, stoppingToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "OrderPlaced {MessageId} failed in a way redelivery cannot fix; dropping it. Body: {Body}",
                messageId,
                body);

            await AckAsync(channel, args.DeliveryTag, stoppingToken);
        }
    }

    /// <summary>What happened to one delivery.</summary>
    private enum Outcome
    {
        Reserved,
        Rejected,
        Duplicate
    }

    /// <summary>
    /// Holds every line of one order, or none of them, and writes the outcome in the same transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All the stock items are read in <b>one statement</b>. Reading them one per line would give each
    /// its own READ COMMITTED snapshot, which is the shape that produced Stock's read-skew 500 (D15):
    /// a decision computed from rows of mixed vintage is not a decision about any state that existed.
    /// Here each line's decision spans only its own item, so the failure mode is narrower than D15's —
    /// but one snapshot for the whole order is strictly safer and costs one query.
    /// </para>
    /// <para>
    /// The duplicate check is the database's, not a pre-flight <c>AnyAsync</c>. §4.5 is explicit that a
    /// SELECT-then-INSERT test has a race window and the constraint does not, and the inbox primary key
    /// is that constraint.
    /// </para>
    /// </remarks>
    private async Task<Outcome> ReserveAsync(
        Guid messageId,
        string? traceParent,
        OrderPlaced payload,
        CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Hoisted out of the scope below because the rejection path deliberately runs in a *fresh*
        // context, and it has to know why the first one was discarded.
        RejectionReason? reason = null;
        var failedProductId = Guid.Empty;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

            var productIds = payload.Lines.Select(line => line.ProductId).ToList();

            var items = await db.StockItems
                .Where(item => productIds.Contains(item.ProductId))
                .ToListAsync(stoppingToken);

            var byProduct = items.ToDictionary(item => item.ProductId);

            var reservations = new List<StockReservation>(payload.Lines.Count);
            var reservedLines = new List<StockReservedLine>(payload.Lines.Count);

            foreach (var line in payload.Lines)
            {
                if (!byProduct.TryGetValue(line.ProductId, out var item))
                {
                    reason = RejectionReason.UnknownProduct;
                    failedProductId = line.ProductId;
                    break;
                }

                try
                {
                    // Guards before mutation, so a refusal leaves the entity byte-identical.
                    item.Reserve(line.Quantity);
                }
                catch (InsufficientStockException)
                {
                    reason = RejectionReason.InsufficientStock;
                    failedProductId = line.ProductId;
                    break;
                }

                reservations.Add(StockReservation.Create(item.Id, payload.OrderId, line.Quantity));

                // Built from the requested line rather than read back off the reservation, because a
                // reservation carries the *stock item's* id and the product id lives on StockItem. The
                // event speaks in product ids: the saga reads Catalog's products, not Stock's internal
                // rows, and handing it a stock item id would invite a cross-service key.
                reservedLines.Add(new StockReservedLine(line.ProductId, line.Quantity));
            }

            if (reason is null)
            {
                var reserved = new StockReserved(
                    Guid.NewGuid(),
                    payload.OrderId,
                    now,
                    reservedLines);

                db.InboxMessages.Add(InboxMessage.Create(
                    messageId, StockEventTopology.OrderPlacedRoutingKey, now));

                db.StockReservations.AddRange(reservations);

                db.OutboxMessages.Add(OutboxMessage.Create(
                    reserved.MessageId,
                    StockEventTopology.StockExchange,
                    StockEventTopology.ReservedRoutingKey,
                    JsonSerializer.Serialize(reserved, StockMessageJson.Options),
                    traceParent,
                    now));

                try
                {
                    await db.SaveChangesAsync(stoppingToken);

                    return Outcome.Reserved;
                }
                catch (DbUpdateException exception)
                    when (ConstraintName(exception) == InboxMessageConfiguration.PrimaryKeyName)
                {
                    // A concurrent delivery of the same message won. Its transaction carried the
                    // inbox row, the holds and the outcome event together, so nothing is missing.
                    return Outcome.Duplicate;
                }
            }

            // The rejection has to be written without the mutations this context is already carrying
            // from the lines that succeeded before the failure. Discarding it and opening a fresh one
            // is what keeps the domain's availability rule in the domain: re-checking `Available` here
            // to avoid the mutations would be a second copy of a guard the entity owns.
        }

        var rejected = new StockReservationRejected(
            Guid.NewGuid(),
            payload.OrderId,
            reason!.Value,
            failedProductId,
            now);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockDbContext>();

            db.InboxMessages.Add(InboxMessage.Create(
                messageId, StockEventTopology.OrderPlacedRoutingKey, now));

            db.OutboxMessages.Add(OutboxMessage.Create(
                rejected.MessageId,
                StockEventTopology.StockExchange,
                StockEventTopology.ReservationRejectedRoutingKey,
                JsonSerializer.Serialize(rejected, StockMessageJson.Options),
                traceParent,
                now));

            try
            {
                await db.SaveChangesAsync(stoppingToken);

                logger.LogInformation(
                    "OrderPlaced {MessageId} rejected for order {OrderId}: {Reason} on product {ProductId}",
                    messageId,
                    payload.OrderId,
                    rejected.Reason,
                    failedProductId);

                return Outcome.Rejected;
            }
            catch (DbUpdateException exception)
                when (ConstraintName(exception) == InboxMessageConfiguration.PrimaryKeyName)
            {
                return Outcome.Duplicate;
            }
        }
    }

    /// <summary>
    /// Declares the topology this consumer needs, then subscribes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both exchanges are declared here, including Ordering's. Declaration is idempotent, and a
    /// consumer that starts before its producer must not fail on a binding to an exchange that does
    /// not exist yet — which is a real ordering, not a hypothetical one, since nothing pins which
    /// service boots first.
    /// </para>
    /// <para>
    /// The queue is <b>durable</b>, and not by preference: RabbitMQ 4 rejects a transient
    /// non-exclusive queue outright with <c>code=541, "Feature `transient_nonexcl_queues` is
    /// deprecated"</c>, and exclusive is not available to a consumer that must survive a restart.
    /// </para>
    /// <para>
    /// <c>consumerDispatchConcurrency: 1</c> is set explicitly rather than left to the library
    /// default. Serial dispatch is what keeps this consumer from racing itself on
    /// <c>stock_items</c>, and the default is a library decision nobody here pinned — the channel-level
    /// option does not inherit the factory value unless it is passed as <c>null</c>, so relying on
    /// either is relying on something that can change underneath.
    /// </para>
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
                    exchange: StockEventTopology.OrdersExchange,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    noWait: false,
                    cancellationToken: stoppingToken);

                await channel.ExchangeDeclareAsync(
                    exchange: StockEventTopology.StockExchange,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    noWait: false,
                    cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync(
                    queue: StockEventTopology.OrderPlacedQueue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    noWait: false,
                    cancellationToken: stoppingToken);

                await channel.QueueBindAsync(
                    queue: StockEventTopology.OrderPlacedQueue,
                    exchange: StockEventTopology.OrdersExchange,
                    routingKey: StockEventTopology.OrderPlacedRoutingKey,
                    arguments: null,
                    noWait: false,
                    cancellationToken: stoppingToken);

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
    /// Reads the delivery's identity and payload. Returns false for anything malformed.
    /// </summary>
    /// <remarks>
    /// The message id comes from the AMQP property rather than the body, because that is the identity a
    /// broker redelivers unchanged and the one an inbox is documented to key on. The producer sets both
    /// from one value and a test pins that they agree.
    /// </remarks>
    private bool TryReadMessage(
        BasicDeliverEventArgs args,
        string body,
        out Guid messageId,
        out string? traceParent,
        out OrderPlaced payload)
    {
        messageId = Guid.Empty;
        payload = null!;
        traceParent = ReadTraceParent(args);

        var propertyMessageId = args.BasicProperties.MessageId;

        if (propertyMessageId is null || !Guid.TryParse(propertyMessageId, out messageId))
        {
            logger.LogError(
                "Delivery arrived with no parseable AMQP messageId, so it cannot be deduplicated; dropping it. Body: {Body}",
                body);

            return false;
        }

        try
        {
            var deserialized = JsonSerializer.Deserialize<OrderPlaced>(body, StockMessageJson.Options);

            if (deserialized is null || deserialized.Lines is null || deserialized.Lines.Count == 0)
            {
                logger.LogError(
                    "OrderPlaced {MessageId} deserialised to nothing usable; dropping it. Body: {Body}",
                    messageId,
                    body);

                return false;
            }

            payload = deserialized;

            return true;
        }
        catch (JsonException exception)
        {
            logger.LogError(
                "OrderPlaced {MessageId} is not valid JSON, so redelivery cannot help; dropping it. {Message} Body: {Body}",
                messageId,
                exception.Message,
                body);

            return false;
        }
    }

    /// <summary>
    /// The inbound <c>traceparent</c>, in whichever of the two shapes a broker may deliver a header
    /// value. That encoding is the broker's choice rather than ours, so this is a boundary and both are
    /// handled.
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
    /// Makes the inbound trace ambient, so every log line this delivery writes carries the producer's
    /// trace id rather than a fresh one.
    /// </summary>
    /// <remarks>
    /// An <see cref="Activity"/> is created directly rather than through an <c>ActivitySource</c>,
    /// because <c>ActivitySource.StartActivity</c> returns null when nothing is listening — and the
    /// source this would use is not registered with the tracer provider, since
    /// <c>AgenticShop.Shared/Tracing</c> configures instrumentation for all three services and a
    /// Stock-specific source name does not belong there. Creating the activity directly means
    /// correlation works unconditionally. The cost is recorded in <c>docs/KNOWN-ISSUES.md</c>: these
    /// spans are not exported to OTLP until the source is registered, so the consumer's work is
    /// correctly correlated in logs but absent from the trace tree.
    /// </remarks>
    private static Activity? StartActivity(string? traceParent)
    {
        var activity = new Activity(ActivityName);

        // No IdFormat assignment: the property is read-only, and SetParentId parses a W3C
        // traceparent and sets the format from it. Activity.DefaultIdFormat is already W3C on .NET 5+,
        // so an activity with no parent is W3C too.
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
