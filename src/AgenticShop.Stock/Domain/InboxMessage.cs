namespace AgenticShop.Stock.Domain;

/// <summary>
/// One inbound message this service has already acted on, keyed by the producer's message id.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is at-least-once, so the same message can arrive twice: a publish acknowledged by the
/// broker and then lost before its outbox row was saved, a consumer that crashed after committing but
/// before acking, or a requeue after a transient conflict. Without this row, "have I already done
/// this?" has no answer, and the second delivery reserves a second set of holds.
/// </para>
/// <para>
/// <b>Stock's <c>UNIQUE(order_id, stock_item_id)</c> index does not substitute for it</b>, even though
/// it looks like it should. That index catches a duplicate only when the first attempt <i>succeeded</i>
/// and left a reservation row to collide with. A first attempt that was <b>refused</b> writes no
/// reservation at all, so a redelivery has nothing to conflict against — and if stock arrived in
/// between, the redelivery now succeeds and publishes <c>StockReserved</c> for an order already
/// answered with <c>StockReservationRejected</c>. Two contradictory outcomes for one message. The
/// index also cannot say whether the outcome event was published, only that a hold exists; this row,
/// written in the same transaction as the hold and the outbox row, is what makes "processed" and
/// "answered" one fact instead of two.
/// </para>
/// <para>
/// The unique index on <c>order_id</c> stays, and stays load-bearing: it is what stops a genuinely
/// duplicated <i>request</i>, and it is the constraint the HTTP handler branches on to answer 409.
/// This table is the message-level twin of that row-level guarantee, not a replacement for it.
/// </para>
/// </remarks>
public class InboxMessage
{
    /// <summary>
    /// The routing key the message arrived on, kept so a row says what it was rather than only that
    /// something was processed. Bounded because it lands in a column and in log lines.
    /// </summary>
    public const int MaxEventTypeLength = 64;

    /// <summary>EF Core materialisation only.</summary>
    private InboxMessage()
    {
    }

    /// <summary>
    /// The producer's message id, and the primary key. Not a surrogate: the whole point of the row is
    /// that a given message is processed at most once, so the identity <i>is</i> the key — the same
    /// reasoning <c>OrderIdempotencyKey</c> gives for keying on the client's key rather than on a
    /// generated id.
    /// </summary>
    public Guid MessageId { get; private set; }

    public string EventType { get; private set; } = null!;

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public static InboxMessage Create(Guid messageId, string eventType, DateTimeOffset receivedAtUtc)
    {
        // Guards run before anything is constructed, so a rejected call leaves no partial row.
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A message id is required.", nameof(messageId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eventType.Length, MaxEventTypeLength, nameof(eventType));

        return new InboxMessage
        {
            MessageId = messageId,
            EventType = eventType,
            ReceivedAtUtc = receivedAtUtc
        };
    }
}
