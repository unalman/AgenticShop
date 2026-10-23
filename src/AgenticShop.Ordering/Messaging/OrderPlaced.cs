using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticShop.Ordering.Messaging;

/// <summary>
/// The event Ordering publishes when an order has been placed, and the first message contract in the
/// repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not an HTTP DTO and deliberately does not implement <c>IRequestContract</c>.</b>
/// <c>RequestContractCoverageTests</c> reflects over this assembly for that interface to prove every
/// request contract is registered with the validation filter, and anchoring a message contract to it
/// would drag this record into assertions about DataAnnotations that have no meaning for it. A
/// message contract is validated by being deserialisable, not by a filter.
/// </para>
/// <para>
/// <b>It carries what a consumer needs to act and nothing more</b> — order identity, the lines to
/// reserve, the currency they were priced in, and when it happened. No prices: Stock holds no money
/// at all, and a price here would be a second source of truth for a number Catalog owns. No status
/// and no totals: a consumer that needs those is reading a projection, not reacting to an event.
/// Every field added later is a versioning obligation, so the bar for adding one is "some consumer
/// needs it", not "it might be useful".
/// </para>
/// <para>
/// <see cref="MessageId"/> is duplicated between the payload and the AMQP <c>messageId</c> property,
/// both set from the same outbox row id. The property is what a consumer's inbox keys on; the payload
/// copy is there so the value survives a log line, a dead-letter inspection and any intermediary that
/// republishes without preserving properties.
/// </para>
/// </remarks>
/// <param name="MessageId">The outbox row's id. Stable across redelivery, which is what makes it usable as a deduplication key.</param>
/// <param name="OrderId">The order this event describes. Cross-service references are ids, never keys.</param>
/// <param name="OrderNumber">The human reference, carried so an operator can find the order from a message alone.</param>
/// <param name="Currency">One currency for the whole order, snapshotted from Catalog at placement.</param>
/// <param name="OccurredAtUtc">When the order was placed — not when the message was published, which a broker outage can delay arbitrarily.</param>
/// <param name="Lines">What to reserve. One entry per product; the placement path has already combined duplicates.</param>
public sealed record OrderPlaced(
    Guid MessageId,
    Guid OrderId,
    string OrderNumber,
    string Currency,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<OrderPlacedLine> Lines)
{
    /// <summary>
    /// The serialisation settings both sides of this contract must agree on. Web defaults give
    /// camelCase property names and case-insensitive reading, which is what the HTTP contracts
    /// already produce, so a message and a response body look the same.
    /// </summary>
    /// <remarks>
    /// Declared here rather than left to each call site's defaults, because a consumer that
    /// deserialised with different settings would read every property as null and the failure would
    /// look like an empty event rather than a mismatch. Stock declares its own copy: the two
    /// assemblies must not reference each other, and this is the same accepted duplication as the
    /// test harness.
    /// </remarks>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Written out rather than inferred, so a future enum member serialises as its name and a
        // consumer does not have to know the ordinal a value happened to have when it was published.
        Converters = { new JsonStringEnumConverter() }
    };

    public static OrderPlaced From(Domain.Order order, Guid messageId) => new(
        messageId,
        order.Id,
        order.OrderNumber,
        order.Currency,
        order.PlacedAtUtc,
        order.Lines
            .OrderBy(line => line.ProductId)
            .Select(line => new OrderPlacedLine(line.ProductId, line.Quantity))
            .ToList());
}

/// <summary>One product to reserve, and how many of it.</summary>
public sealed record OrderPlacedLine(Guid ProductId, int Quantity);

/// <summary>
/// Where <see cref="OrderPlaced"/> is published. Named in one place so the publisher, the tests and
/// — later — the routing cannot disagree about a string.
/// </summary>
/// <remarks>
/// A topic exchange rather than the default one, so a consumer binds a routing-key pattern instead of
/// sharing a queue name with the producer. That is what lets a second consumer subscribe to
/// <c>order.*</c> later without Ordering knowing it exists, and it is why the exchange is durable: a
/// message published to a non-durable exchange is dropped by a broker restart even if the queue it
/// routed to survived.
/// </remarks>
public static class OrderEventTopology
{
    public const string Exchange = "agenticshop.orders";

    public const string OrderPlacedRoutingKey = "order.placed";
}
