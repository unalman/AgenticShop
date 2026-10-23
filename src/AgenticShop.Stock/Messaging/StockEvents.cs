using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticShop.Stock.Messaging;

/// <summary>
/// What Stock publishes when it has finished with an <see cref="OrderPlaced"/>: either every line was
/// taken from inventory, or none was.
/// </summary>
/// <remarks>
/// <para>
/// <b>All-or-nothing per order, in one transaction.</b> The synchronous path this replaces reserved
/// line by line and released the earlier holds when a later one failed. Here the whole order is a unit
/// of work: every line is taken and the outcome row is written in one <c>SaveChangesAsync</c>, or
/// nothing is and a rejection is written instead. That removes the compensation the synchronous path
/// needed, because there is no intermediate state to undo — and it is why <see cref="StockCommitted"/>
/// carries the lines back rather than a count.
/// </para>
/// <para>
/// <b>Committed, not merely held.</b> The consumer reserves and confirms in the same transaction, so by
/// the time this event exists the units have already left <c>quantity_on_hand</c> and the reservation
/// row is <c>Confirmed</c>. An earlier revision of this design held in <c>Pending</c> and left
/// confirmation to a saga; it changed because the two-phase split existed to let Ordering undo a
/// <i>partial</i> hold, and an all-or-nothing hold has no partial state to undo. See D16's revision —
/// the earlier reasoning was not careless, it was answering a question the design no longer has.
/// </para>
/// <para>
/// The cost is recorded rather than hidden: <c>Confirmed</c> is terminal (D3), so if cancellation ever
/// arrives there is nothing left to release. <c>StockItem.ReleaseReservation</c> is kept for that
/// conversation, which is Phase 3's at the earliest.
/// </para>
/// </remarks>
public sealed record StockCommitted(
    Guid MessageId,
    Guid OrderId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<StockCommittedLine> Lines);

/// <summary>One line that was taken from inventory.</summary>
public sealed record StockCommittedLine(Guid ProductId, int Quantity);

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

    /// <summary>
    /// Named for the effect on inventory rather than for the row it writes. The table is
    /// <c>stock_reservations</c> and the entity is <see cref="Domain.StockReservation"/>, because that
    /// is the audit record; the event says what happened to stock, because that is what a consumer
    /// acts on. The two vocabularies deliberately differ — <c>stock.reserved</c> would have invited a
    /// consumer to assume the units were still merely held and could be given back.
    /// </summary>
    public const string CommittedRoutingKey = "stock.committed";

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
