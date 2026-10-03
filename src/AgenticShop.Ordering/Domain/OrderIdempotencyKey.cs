namespace AgenticShop.Ordering.Domain;

/// <summary>
/// One client-supplied <c>Idempotency-Key</c> and the order it produced, so a retried
/// <c>POST /orders</c> replays the first attempt instead of placing a second order.
/// </summary>
/// <remarks>
/// <para>
/// This is the only mutable row Ordering owns, and it exists because placement is not idempotent by
/// itself: a retry mints a fresh order id, reserves a second set of holds, and — for a first attempt
/// that reached <see cref="OrderStatus.Confirmed"/> — ships twice. Stock's
/// <c>UNIQUE(order_id, stock_item_id)</c> cannot help, because the retry carries a different order
/// id. Catalog's unique SKU cannot help for the same reason. Only the caller knows that two requests
/// are the same request, and the key is how it says so.
/// </para>
/// <para>
/// <b>A row exists in two states, and the difference matters.</b> Claimed but not completed —
/// <see cref="OrderId"/> is null — means a placement is in flight, or the process died holding the
/// claim. Completed means the order row exists and <see cref="StatusCode"/> is what the caller was
/// told. The claim is written before any side effect, and the completion is written in the same
/// <c>SaveChangesAsync</c> as the order, so there is no window in which an order exists without its
/// key pointing at it.
/// </para>
/// <para>
/// The failure direction is deliberately <b>fail closed</b>: a stranded claim blocks that one key
/// forever, but it can never cause a second placement. That is the same trade the stranded-hold
/// residual makes, and Phase 3's expiry worker is what lifts both.
/// </para>
/// </remarks>
public class OrderIdempotencyKey
{
    /// <summary>
    /// Room for a GUID (36) or a prefixed token, while bounding how much untrusted text reaches a
    /// primary key column, the log and — through <see cref="IdempotencyKeyInUseException"/> — an
    /// operator's screen.
    /// </summary>
    public const int MaxKeyLength = 64;

    private OrderIdempotencyKey()
    {
    }

    public string Key { get; private set; } = null!;

    /// <summary>Null until the placement completes. Null means the claim is still open.</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>
    /// The status the first attempt answered with, replayed verbatim.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived from <see cref="Order.Status"/>, because the mapping is not
    /// one-to-one: a <see cref="OrderStatus.Failed"/> order came from either a 409 (stock refused)
    /// or a 502 (Stock unreachable during reserve), and replaying the wrong one would tell a client
    /// to fix its basket when the real problem was ours.
    /// </remarks>
    public int? StatusCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// True once the placement finished and the order row exists. Derived, not stored.
    /// </summary>
    public bool IsCompleted => OrderId is not null;

    public static OrderIdempotencyKey Create(string key, DateTimeOffset createdAtUtc)
    {
        // Guards run before any mutation, so a rejected key leaves nothing behind.
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, MaxKeyLength, nameof(key));

        // The key is untrusted and ends up in a primary key column and in log lines, so it is held
        // to the same character set as an inbound correlation id: no control characters, no
        // whitespace, nothing that could reshape a log line or need escaping in a header.
        if (!key.All(IsAllowedCharacter))
        {
            throw new ArgumentException(
                "An idempotency key may contain only ASCII letters, digits, '-', '_' and '.'.",
                nameof(key));
        }

        return new OrderIdempotencyKey
        {
            Key = key,
            CreatedAtUtc = createdAtUtc
        };
    }

    /// <summary>
    /// Records the order this key produced and the status the caller was given.
    /// </summary>
    /// <remarks>
    /// Strict rather than idempotent: completing an already-completed key throws. Only the request
    /// that won the claim can reach this, so a second call means this assembly has a bug, and
    /// silently overwriting the recorded status would corrupt every later replay.
    /// </remarks>
    public void Complete(Guid orderId, int statusCode)
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException(
                $"Idempotency key '{Key}' is already completed for order {OrderId}.");
        }

        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("An order id is required.", nameof(orderId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 100, nameof(statusCode));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599, nameof(statusCode));

        OrderId = orderId;
        StatusCode = statusCode;
    }

    private static bool IsAllowedCharacter(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}
