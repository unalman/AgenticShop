namespace AgenticShop.Ordering.Messaging;

/// <summary>
/// Every line of an order was held. Ordering's copy of Stock's contract, not a reference to it.
/// </summary>
/// <remarks>
/// <para>
/// Duplicated for the reason <see cref="OrderPlaced"/> gives on the other side: the build guard
/// forbids a reference between two services, and each service's boundary test pins its referenced
/// <c>AgenticShop.*</c> set. What keeps the copies honest is a test against the bytes that actually
/// cross the wire — <c>StockEventContractTests</c> deserialises a payload captured from Stock's
/// publisher rather than a hand-written mirror of what I believe it sends.
/// </para>
/// <para>
/// Deserialised with <see cref="OrderPlaced.JsonOptions"/> rather than a second set of options. One
/// assembly has one wire format; a consumer that read inbound events with different settings than the
/// producer's outbound ones would read every property as a default, and the failure would look like an
/// empty event rather than a mismatch.
/// </para>
/// </remarks>
/// <param name="MessageId">The producing outbox row's id. Stable across redelivery, and the key the inbox deduplicates on.</param>
/// <param name="OrderId">The order that was held.</param>
/// <param name="OccurredAtUtc">When Stock decided, not when it published — a broker outage can separate the two arbitrarily.</param>
/// <param name="Lines">What was held. Echoed back so the saga can reconcile against the order without reading Stock.</param>
public sealed record StockReserved(
    Guid MessageId,
    Guid OrderId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<StockReservedLine> Lines);

/// <summary>One hold Stock took.</summary>
public sealed record StockReservedLine(Guid ProductId, int Quantity);

/// <summary>
/// Nothing was held, and why. All-or-nothing per order on the producing side, so there is no partial
/// outcome to represent — which is what makes the transition here binary.
/// </summary>
/// <param name="MessageId">The producing outbox row's id.</param>
/// <param name="OrderId">The order nothing was held for.</param>
/// <param name="Reason">The category of refusal, carried so the saga need not read Stock's state to know it.</param>
/// <param name="FailedProductId">The product that could not be held. Identifies a row in Catalog's database, not one in Stock's.</param>
/// <param name="OccurredAtUtc">When the refusal was decided.</param>
public sealed record StockReservationRejected(
    Guid MessageId,
    Guid OrderId,
    RejectionReason Reason,
    Guid FailedProductId,
    DateTimeOffset OccurredAtUtc);

/// <summary>Why Stock refused a hold.</summary>
/// <remarks>
/// A copy of Stock's enum. Read by name rather than by ordinal, so a member inserted in the middle of
/// Stock's list cannot silently change what an already-published message means.
/// </remarks>
public enum RejectionReason
{
    /// <summary>The product is not in Stock's catalogue of items at all.</summary>
    UnknownProduct,

    /// <summary>The item exists but not enough of it is available.</summary>
    InsufficientStock
}

/// <summary>
/// Where Ordering listens for Stock's outcomes.
/// </summary>
/// <remarks>
/// Bound to the two routing keys it actually handles rather than to <c>stock.#</c>. A wildcard binding
/// is convenient the day a third event type appears, and quietly wrong every day before that: an
/// unhandled event would be delivered, acknowledged and ignored, which reads from the outside exactly
/// like an event that was handled. Adding a binding is one line; discovering that a saga silently
/// dropped an event type is not.
/// </remarks>
public static class StockEventTopology
{
    /// <summary>Stock's exchange. Declared idempotently on connect, because a consumer that starts before its producer must not fail on a missing exchange.</summary>
    public const string StockExchange = "agenticshop.stock";

    public const string ReservedRoutingKey = "stock.reserved";

    public const string ReservationRejectedRoutingKey = "stock.reservation-rejected";

    /// <summary>Ordering's own queue. Consumer-owned, durable, named after what it consumes.</summary>
    public const string OutcomeQueue = "ordering.stock-outcomes";

    /// <summary>
    /// One unacked delivery at a time. Serial dispatch comes from the client's dispatch concurrency
    /// rather than from this, but both are pinned because the consumer updates a row and a redelivery
    /// racing the original is exactly what the <c>xmin</c> token would have to catch.
    /// </summary>
    public const ushort PrefetchCount = 1;
}
