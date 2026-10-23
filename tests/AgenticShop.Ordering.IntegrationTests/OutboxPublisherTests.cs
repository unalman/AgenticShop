using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.Endpoints;
using AgenticShop.Ordering.Messaging;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// That a committed outbox row actually reaches the broker, and that what arrives is the message that
/// was written.
/// </summary>
/// <remarks>
/// <para>
/// This needs a real broker and cannot be faked honestly. A fake publisher would prove only that
/// Ordering called the fake — it could not show that publisher confirms are enabled, that the
/// exchange is durable, that the message is persistent, or that the traceparent survives the hop as an
/// AMQP header rather than as a field nobody reads. Those are properties of the wire.
/// </para>
/// <para>
/// The topology is declared here with <b>exactly</b> the parameters <see cref="OutboxPublisher"/>
/// uses. That is not fastidiousness: AMQP answers a redeclaration with different parameters as
/// <c>PRECONDITION_FAILED</c> and closes the channel, so a test that drifted from the publisher would
/// fail as a connection error rather than as a mismatch. The same strictness is what makes the
/// declaration a check — if the publisher ever stops declaring the exchange durable, this test's
/// durable redeclaration is what objects.
/// </para>
/// </remarks>
[Collection("Ordering Messaging")]
public sealed class OutboxPublisherTests(OrderingMessagingFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// The test's own queue. Named rather than server-generated so a leftover message is findable,
    /// and purged before each test so one test never consumes another's message.
    /// </summary>
    private const string QueueName = "ordering.integration-tests.order-placed";

    /// <summary>
    /// Long enough for a background poll that wakes every second, short enough that a real failure is
    /// not mistaken for slowness after a minute of waiting.
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(20);

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await DeclareAndPurgeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ACommittedOrderPlacedIsPublishedAndItsRowIsMarkedPublished()
    {
        var product = AddProduct(name: "Blue Widget", price: 12.50m);

        var response = await PlaceAsync(product, quantity: 2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await BodyAsync(response);
        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();

        // Arrives, and arrives as what was written.
        var delivered = await ConsumeOneAsync(DeliveryTimeout);

        delivered.ContentType.Should().Be("application/json");
        delivered.Persistent.Should().BeTrue(
            "a message the outbox believes it delivered must survive a broker restart, or the row is " +
            "marked published and the event is simply gone");

        var payload = JsonSerializer.Deserialize<OrderPlaced>(delivered.Body, OrderPlaced.JsonOptions);

        payload.Should().NotBeNull();
        payload!.OrderId.Should().Be(body.Id);
        payload.OrderNumber.Should().Be(body.OrderNumber);
        payload.Currency.Should().Be(body.Currency);
        payload.OccurredAtUtc.Should().Be(body.PlacedAtUtc);
        payload.Lines.Should().ContainSingle();
        payload.Lines[0].ProductId.Should().Be(product);
        payload.Lines[0].Quantity.Should().Be(2);

        // The AMQP property and the payload copy are set from one value; this is what proves it.
        delivered.MessageId.Should().Be(payload.MessageId.ToString(),
            "a consumer's inbox keys on the AMQP messageId, so it must be the id the payload carries");

        delivered.TraceParent.Should().NotBeNull(
            "without it the trace stops at the broker and the consumer's work is an orphan tree");
        delivered.TraceParent!.Should().Contain(correlationId,
            "the correlation id is the W3C trace id, so the message must carry the request's trace across the hop");

        // The row is only marked once the broker has accepted it, which is what publisher confirms
        // buy. Asserted after delivery so the ordering is not a guess about timing.
        var row = await WaitForPublishedRowAsync(payload.MessageId, DeliveryTimeout);

        row.PublishedAtUtc.Should().NotBeNull();
        row.PublishedAtUtc!.Value.Should().BeOnOrAfter(row.CreatedAtUtc);
        row.Id.Should().Be(payload.MessageId);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private async Task DeclareAndPurgeAsync() => await WithChannelAsync(async channel =>
    {
        await channel.ExchangeDeclareAsync(
            exchange: OrderEventTopology.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null);

        // Durable, and not by preference. RabbitMQ 4 rejects a transient non-exclusive queue outright
        // (code 541, "Feature `transient_nonexcl_queues` is deprecated ... not permitted anymore"),
        // and exclusive is not available here either: this method declares on one connection and
        // ConsumeOneAsync reads on another, so an exclusive queue would be gone by then. Durable is
        // also the honest choice — it is what Phase 2's consumer queue will be, which is why compose
        // gives the broker a named volume. The container is fresh per run and the queue is purged per
        // test, so durability costs nothing here.
        await channel.QueueDeclareAsync(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        await channel.QueueBindAsync(
            queue: QueueName,
            exchange: OrderEventTopology.Exchange,
            routingKey: OrderEventTopology.OrderPlacedRoutingKey,
            arguments: null);

        await channel.QueuePurgeAsync(QueueName);
    });

    private async Task<Delivered> ConsumeOneAsync(TimeSpan timeout)
    {
        var delivered = new TaskCompletionSource<Delivered>(TaskCreationOptions.RunContinuationsAsynchronously);

        await WithChannelAsync(async channel =>
        {
            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += (_, args) =>
            {
                delivered.TrySetResult(Capture(args));

                return Task.CompletedTask;
            };

            await channel.BasicConsumeAsync(queue: QueueName, autoAck: true, consumer: consumer);

            // The subscription has to outlive this method, so the wait happens here rather than
            // after the channel is disposed.
            using var cts = new CancellationTokenSource(timeout);

            await using var registration = cts.Token.Register(() => delivered.TrySetCanceled(cts.Token));

            await delivered.Task.WaitAsync(cts.Token);
        });

        return await delivered.Task;
    }

    /// <summary>
    /// Copies what the assertion needs out of the delivery.
    /// </summary>
    /// <remarks>
    /// The body has to be copied inside the handler: the library owns that memory and it is only valid
    /// for the duration of the callback, so returning it and reading it later is a use-after-free that
    /// would show up as garbage rather than as an error. Header values are handled in both of the
    /// shapes a broker may send — a string or a byte array — because that is an external boundary and
    /// the encoding is the broker's choice, not ours.
    /// </remarks>
    private static Delivered Capture(BasicDeliverEventArgs args)
    {
        var properties = args.BasicProperties;

        string? traceParent = null;

        if (properties.Headers is { } headers
            && headers.TryGetValue("traceparent", out var value)
            && value is not null)
        {
            traceParent = value is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : value.ToString();
        }

        return new Delivered(
            Encoding.UTF8.GetString(args.Body.ToArray()),
            properties.MessageId,
            properties.ContentType,
            traceParent,
            Persistent: properties.DeliveryMode == DeliveryModes.Persistent);
    }

    private async Task WithChannelAsync(Func<IChannel, Task> action)
    {
        var factory = new ConnectionFactory { Uri = new Uri(fixture.BrokerUrl) };

        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await action(channel);
    }

    private async Task<OutboxMessage> WaitForPublishedRowAsync(Guid messageId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

            var row = await db.OutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(message => message.Id == messageId);

            if (row?.IsPublished == true)
            {
                return row;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            $"Outbox message {messageId} was delivered to the broker but its row was still marked " +
            $"unpublished after {timeout.TotalSeconds:F0}s, so marking and publishing have come apart.");
    }

    private Guid AddProduct(string name, decimal price)
    {
        var productId = Guid.NewGuid();

        fixture.Catalog.WithProduct(productId, name, price, "USD", isActive: true);

        return productId;
    }

    private async Task<HttpResponseMessage> PlaceAsync(Guid productId, int quantity)
    {
        var request = new CreateOrderRequest([new CreateOrderLineRequest(productId, quantity)]);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };

        message.Headers.Add(
            OrderEndpoints.IdempotencyKeyHeader,
            Guid.NewGuid().ToString("N"));

        return await fixture.Client.SendAsync(message);
    }

    private static async Task<OrderResponse> BodyAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<OrderResponse>())!;

    private sealed record Delivered(
        string Body,
        string? MessageId,
        string? ContentType,
        string? TraceParent,
        bool Persistent);
}
