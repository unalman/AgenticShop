namespace AgenticShop.Ordering.Domain;

/// <summary>
/// One inbound outcome event this service has already acted on, keyed by the producer's message id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unlike Stock's inbox, this one is not what makes redelivery safe.</b> Stock needed its inbox
/// because a refused hold writes no reservation row, so a redelivery had nothing to collide with and
/// could take a second hold. Here the domain already refuses the second settlement:
/// <see cref="Order.TransitionTo"/> is reachable only from <c>Pending</c>, so a redelivered
/// <c>StockCommitted</c> finds a terminal order and changes nothing. Copying Stock's justification
/// across would have been a plausible sentence and a wrong one.
/// </para>
/// <para>
/// What this row actually buys is the ability to <b>tell two situations apart</b>. A consumer that
/// finds an already-settled order is looking at one of:
/// </para>
/// <list type="number">
/// <item>
/// a redelivery of the message that settled it — ordinary at-least-once behaviour, worth a
/// <c>Information</c> line and an acknowledgement;
/// </item>
/// <item>
/// a <b>different</b> message for the same order, meaning Stock published two outcomes for one
/// <c>OrderPlaced</c> — a producer bug, worth an <c>Error</c> and someone's attention.
/// </item>
/// </list>
/// <para>
/// Without the inbox both look identical: an event arrived and the order was already settled. With it,
/// the message id answers the question. That is the whole justification, and it is an observability one
/// rather than a correctness one — §4.7 makes the severity split load-bearing, and a consumer that
/// cannot distinguish these two cannot honour it.
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
    /// The producer's message id, and the primary key. Not a surrogate: the row's whole purpose is to
    /// answer "have I seen this message", so the identity <i>is</i> the key — the same reasoning
    /// <see cref="OrderIdempotencyKey"/> gives for keying on the client's key.
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
