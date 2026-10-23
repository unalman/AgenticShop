namespace AgenticShop.Ordering.Domain;

/// <summary>
/// One event that has been committed but not yet handed to the broker.
/// </summary>
/// <remarks>
/// <para>
/// This row exists because writing an order and publishing an event cannot be one transaction. The
/// broker has no vote in a PostgreSQL commit, so the two have to be sequenced — and one of the two
/// orders is survivable while the other is not. Publish first and a crash before the commit leaves a
/// message describing an order that does not exist, which no consumer can detect or undo. Commit
/// first and a crash before the publish leaves this row sitting there unpublished, where the next
/// poll picks it up. So the row is written inside the order's transaction and the publish happens
/// afterwards.
/// </para>
/// <para>
/// That choice makes delivery <b>at-least-once</b>, not exactly-once, and the distinction is
/// load-bearing rather than theoretical: a publish that reaches the broker and then loses the
/// connection before the acknowledgement arrives is indistinguishable from one that never left, so
/// the row stays unpublished and the message goes out twice. Exactly-once would need the broker and
/// the database to agree atomically, which is the thing this row exists to avoid needing. The
/// consequence is that a consumer must deduplicate on <see cref="Id"/>, which is what Phase 2's inbox
/// is for.
/// </para>
/// <para>
/// <b>Rows are never deleted and never re-used.</b> <see cref="PublishedAtUtc"/> is a one-way
/// transition, so the table grows without bound. That is accepted at this scale; retention is a
/// Phase 3 concern, recorded in <c>../../../docs/KNOWN-ISSUES.md</c> rather than solved here with a
/// cleanup worker that nothing has asked for yet.
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
    /// The message identity, and the value published as AMQP <c>messageId</c>. Deliberately its own
    /// surrogate rather than the order's id: an inbox keyed on the order id would collide the moment
    /// a second event type per order exists, and two different events for one order are two messages.
    /// </summary>
    /// <remarks>
    /// Supplied by the caller rather than generated here, for the same reason <see cref="Order"/>'s
    /// id is: the payload carries this value too, and the two have to be the same id. Generating it
    /// inside the factory would mean serialising the payload before the row existed, or mutating the
    /// payload afterwards — and a payload that can be rewritten after construction is a second source
    /// of truth for what was published.
    /// </remarks>
    public Guid Id { get; private set; }

    public string Exchange { get; private set; } = null!;

    public string RoutingKey { get; private set; } = null!;

    /// <summary>
    /// The serialised event, captured at write time rather than rendered at publish time. Rendering
    /// later would mean the message reflects whatever the order row says when the publisher happens
    /// to get to it, so a row written before a schema change could be published after one.
    /// </summary>
    public string PayloadJson { get; private set; } = null!;

    /// <summary>
    /// The W3C <c>traceparent</c> of the request that wrote this row, or null if there was no
    /// activity to capture. Carried in a column because the publisher runs on a background loop with
    /// no inbound request and therefore no ambient <c>Activity</c> of its own — without this the
    /// trace would break at the broker hop and the placement's spans would not reach the consumer's.
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
        // parse, which is worse than publishing none. This is not defensive coding against an
        // impossible input: ASP.NET Core starts an activity per request in W3C format, so the value
        // is 55 characters by construction, and reaching this throw means that has changed.
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
    /// Strict rather than idempotent, for the same reason <c>OrderIdempotencyKey.Complete</c> is: one
    /// publisher owns the row, so a second call means this assembly published the same message twice
    /// from one row. Silently overwriting the timestamp would hide that, and the timestamp is the only
    /// evidence that the duplicate happened.
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
