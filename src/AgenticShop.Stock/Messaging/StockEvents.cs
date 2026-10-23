using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticShop.Stock.Messaging;

/// <summary>
/// What Stock publishes when it has finished with an <see cref="OrderPlaced"/>: either every line was
/// held, or none was.
/// </summary>
/// <remarks>
/// <para>
/// <b>All-or-nothing per order, in one transaction.</b> The synchronous path this replaces reserves
/// line by line and releases the earlier holds when a later one fails. Here the whole order is a unit
/// of work: every line is held and the outcome row is written in one <c>SaveChangesAsync</c>, or
/// nothing is and a rejection is written instead. That removes the compensation the synchronous path
/// needed, because there is no intermediate state to undo — and it is why <see cref="StockReserved"/>
/// carries the lines back rather than a count.
/// </para>
/// <para>
/// <b>Reserved, not confirmed.</b> These are holds in <c>Pending</c>, which is what the event name
/// says. Confirming them is the saga's decision, not the consumer's, and the difference is not
/// cosmetic: a <c>Confirmed</c> reservation is terminal (Stock's D3) and Ordering is forbidden from
/// releasing one (its O10). Reserving and confirming in the same breath would therefore throw away the
/// only compensation the saga has. Confirming these holds is Phase 2b's later increment, and until it
/// lands they stay <c>Pending</c>.
/// </para>
/// </remarks>
public sealed record StockReserved(
    Guid MessageId,
    Guid OrderId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<StockReservedLine> Lines);

/// <summary>One hold that was taken.</summary>
public sealed record StockReservedLine(Guid ProductId, int Quantity);

/// <summary>
/// No line was held, and why. Carrying the reason is what lets the saga distinguish "sell less" from
/// "this order cannot be fulfilled at all" without reading another service's state.
/// </summary>
/// <param name="MessageId">This event's own identity, from the outbox row that carried it.</param>
/// <param name="OrderId">The order nothing was held for.</param>
/// <param name="Reason">The category of refusal.</param>
/// <param name="FailedProductId">
/// The product that could not be held, so a rejection names its cause rather than only its category.
/// A <see cref="Guid"/> because it identifies a row in Catalog's database, not one in Stock's — and
/// the saga must not be tempted to treat it as a key into Stock.
/// </param>
/// <param name="OccurredAtUtc">When the refusal was decided.</param>
public sealed record StockReservationRejected(
    Guid MessageId,
    Guid OrderId,
    RejectionReason Reason,
    Guid FailedProductId,
    DateTimeOffset OccurredAtUtc);

/// <summary>Why a hold was refused.</summary>
/// <remarks>
/// Serialised as its name, not its ordinal, so a member inserted in the middle of the list cannot
/// silently change what an already-published message says.
/// </remarks>
public enum RejectionReason
{
    /// <summary>The product is not in Stock's catalogue of items at all.</summary>
    UnknownProduct,

    /// <summary>The item exists but not enough of it is available.</summary>
    InsufficientStock
}

/// <summary>
/// Where Stock's events go, and where it listens.
/// </summary>
/// <remarks>
/// <para>
/// Stock declares <b>its own queue and its own exchange</b>, and binds the queue to Ordering's
/// exchange itself. A consumer owns its queue: that is what keeps the producer ignorant of who is
/// listening, and what lets a second consumer subscribe to <c>order.*</c> later with no change in
/// Ordering. The queue is durable — RabbitMQ 4 refuses a transient non-exclusive one outright
/// (<c>code=541</c>), and a consumer that must not lose work between restarts wants durability anyway.
/// </para>
/// <para>
/// The exchange names follow the domain-per-exchange shape chosen for <c>agenticshop.orders</c>: one
/// topic exchange per publishing service, event types distinguished by routing key.
/// </para>
/// </remarks>
public static class StockEventTopology
{
    /// <summary>Ordering's exchange. Read from, never declared as if it were Stock's to define — but declared idempotently on connect, because a consumer that starts before its producer must not fail.</summary>
    public const string OrdersExchange = "agenticshop.orders";

    public const string OrderPlacedRoutingKey = "order.placed";

    /// <summary>Stock's own exchange, for the outcomes it publishes.</summary>
    public const string StockExchange = "agenticshop.stock";

    public const string ReservedRoutingKey = "stock.reserved";

    public const string ReservationRejectedRoutingKey = "stock.reservation-rejected";

    /// <summary>Stock's work queue. Consumer-owned, durable, and named after what it consumes.</summary>
    public const string OrderPlacedQueue = "stock.order-placed";

    /// <summary>
    /// One unacked delivery at a time.
    /// </summary>
    /// <remarks>
    /// Serial dispatch actually comes from the client's default <c>ConsumerDispatchConcurrency</c> of
    /// one, not from this — prefetch bounds how many unacked deliveries the broker will hand over, and
    /// setting it to one makes "one at a time" true end to end rather than by coincidence of two
    /// defaults. It is set explicitly because the consumer mutates a row other writers also mutate, so
    /// its correctness should not depend on a library default nobody pinned. The cost is throughput,
    /// which is the contention trade <c>docs/ARCHITECTURE.md</c> §7 already measures.
    /// </remarks>
    public const ushort PrefetchCount = 1;
}

/// <summary>
/// The one serialisation setting both of Stock's message directions must agree on.
/// </summary>
/// <remarks>
/// Held once rather than per record, because Stock has three contracts and a consumer that
/// deserialised with different settings than the publisher serialised would read every property as
/// null — a failure that looks like an empty event rather than a mismatch. Web defaults match what the
/// HTTP responses already produce, so a message body and a response body look the same.
/// </remarks>
public static class StockMessageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
