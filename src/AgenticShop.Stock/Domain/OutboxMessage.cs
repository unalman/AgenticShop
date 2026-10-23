namespace AgenticShop.Stock.Domain;

/// <summary>
/// One event that has been committed but not yet handed to the broker.
/// </summary>
/// <remarks>
/// <para>
/// A copy of Ordering's <c>OutboxMessage</c> rather than a shared type, for the reason
/// <see cref="Messaging.OrderPlaced"/> gives: the build guard forbids a reference between two
/// services, and <c>LeafServiceBoundaryTests</c> pins Stock's referenced <c>AgenticShop.*</c> set to
/// exactly the shared library. What keeps two copies honest is that each service's suite asserts the
/// safety properties against its own copy — <c>OutboxPublisherSafetyTests</c> here — because the
/// assembly boundary forbids comparing the two directly.
/// </para>
/// <para>
/// The row exists because committing a reservation and publishing its outcome cannot be one
/// transaction. Publish first and a crash before the commit announces a hold that does not exist;
/// commit first and a crash before the publish leaves the saga waiting forever for an answer that was
/// decided and then lost. So the row is written inside the reservation's transaction — alongside the
/// <see cref="StockItem"/> counter, the <see cref="StockReservation"/> and the
/// <see cref="InboxMessage"/> that deduplicates the delivery — and the publish happens afterwards.
/// </para>
/// <para>
/// That makes delivery <b>at-least-once</b>: a publish acknowledged by the broker and then lost before
/// its row was saved goes out twice. Exactly-once would need the broker and the database to agree
/// atomically, which is the thing this row exists to avoid needing. The consumer of <i>this</i> event
/// therefore needs an inbox of its own, which is what <see cref="InboxMessage"/> is on the other side.
/// </para>
/// <para>
/// <b>Rows are never deleted and never re-used.</b> <see cref="PublishedAtUtc"/> is a one-way
/// transition, so the table grows without bound. Accepted at this scale and recorded in
/// <c>../../../docs/KNOWN-ISSUES.md</c> rather than solved here with a cleanup worker nothing has
/// asked for; retention is Phase 3's.
/// </para>
/// </remarks>
public class OutboxMessage
{
    /// <summary>
    /// AMQP caps an exchange name at 255 octets. Bounded here rather than left to the broker, so an
    /// over-long name is a 500 naming this row instead of a channel exception mid-publish — which
    /// would strand the message and repeat forever.
    /// </summary>
    public const int MaxExchangeLength = 255;

    /// <summary>AMQP caps a routing key at the same length as an exchange name.</summary>
    public const int MaxRoutingKeyLength = 255;

    /// <summary>
    /// Exactly one W3C <c>traceparent</c>: <c>00-</c> plus a 32-hex trace id, a 16-hex parent id and
    /// a 2-hex flags field, with three separators. Sized to the format rather than rounded up, so a
    /// value that does not fit is a bug in the capture and not something to accommodate.
    /// </summary>
    public const int MaxTraceParentLength = 55;

    /// <summary>EF Core materialisation only.</summary>
    private OutboxMessage()
    {
    }

    /// <summary>
    /// The message identity, and the value published as AMQP <c>messageId</c>. Its own surrogate
    /// rather than the order's id, so that a second event type for one order — a later
    /// <c>StockConfirmed</c>, say — cannot collide in a consumer's inbox.
    /// </summary>
    /// <remarks>
    /// Supplied by the caller rather than generated here, because the payload carries this value too
    /// and the two have to be the same id. Generating it inside the factory would mean serialising the
    /// payload before the row existed, or mutating the payload afterwards — and a payload that can be
    /// rewritten after construction is a second source of truth for what was published.
    /// </remarks>
    public Guid Id { get; private set; }

    public string Exchange { get; private set; } = null!;

    public string RoutingKey { get; private set; } = null!;

    /// <summary>
    /// The serialised event, captured at write time rather than rendered at publish time. Rendering
    /// later would mean the message reflects whatever the rows say when the publisher happens to get
    /// to it, so a row written before a contract change could be published after one.
    /// </summary>
    public string PayloadJson { get; private set; } = null!;

    /// <summary>
    /// The W3C <c>traceparent</c> of the delivery that caused this row, or null if the inbound message
    /// carried none. A column because the publisher runs on a background loop with no ambient
    /// <c>Activity</c>: without it the trace breaks at the broker hop and the placement's spans never
    /// reach the work Stock did in response.
    /// </summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Null until the broker has accepted the message. Null means it is still to go.</summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    /// <summary>Derived from <see cref="PublishedAtUtc"/>; not a column.</summary>
    public bool IsPublished => PublishedAtUtc is not null;

    public static OutboxMessage Create(
        Guid id,
        string exchange,
        string routingKey,
        string payloadJson,
        string? traceParent,
        DateTimeOffset createdAtUtc)
    {
        // Guards run before anything is constructed, so a rejected call leaves no partial row.
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A message id is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(exchange.Length, MaxExchangeLength, nameof(exchange));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(routingKey.Length, MaxRoutingKeyLength, nameof(routingKey));

        // Bounded because the column is, and because a value that does not fit a W3C traceparent is
        // not a traceparent — publishing a truncated one would give a consumer a header it cannot
        // parse, which is worse than publishing none. Not defensive coding against an impossible
        // input: the value comes from an inbound AMQP header that a producer set from
        // Activity.Current.Id, and reaching this throw means something upstream changed format.
        if (traceParent is not null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                traceParent.Length, MaxTraceParentLength, nameof(traceParent));
        }

        return new OutboxMessage
        {
            Id = id,
            Exchange = exchange,
            RoutingKey = routingKey,
            PayloadJson = payloadJson,
            TraceParent = string.IsNullOrWhiteSpace(traceParent) ? null : traceParent,
            CreatedAtUtc = createdAtUtc
        };
    }

    /// <summary>Records that the broker accepted this message.</summary>
    /// <remarks>
    /// Strict rather than idempotent, for the same reason <see cref="StockReservation.Confirm"/> is:
    /// one publisher owns the row, so a second call means this assembly published the same message
    /// twice from one row. Silently overwriting the timestamp would hide that, and the timestamp is the
    /// only evidence the duplicate happened.
    /// </remarks>
    public void MarkPublished(DateTimeOffset publishedAtUtc)
    {
        if (IsPublished)
        {
            throw new InvalidOperationException(
                $"Outbox message {Id} was already published at {PublishedAtUtc:O}.");
        }

        PublishedAtUtc = publishedAtUtc;
    }
}
