namespace AgenticShop.Ordering.Domain;

/// <summary>
/// Thrown when a second request presents an <c>Idempotency-Key</c> that is already claimed but not
/// yet completed, so there is no recorded outcome to replay.
/// </summary>
/// <remarks>
/// <para>
/// Two ways to get here. A concurrent duplicate: the first request is still placing and the second
/// raced it. A stranded claim: the process died between claiming the key and writing the order, so
/// the claim will never complete. Both answer the same way, because from outside they are
/// indistinguishable and the correct client behaviour is the same — wait, then retry the same key.
/// </para>
/// <para>
/// Carries the key for the log line only. The client detail is a fixed message: echoing untrusted
/// input back into a response body is exactly what the shared error contract forbids, and it would
/// tell the caller nothing it did not just send.
/// </para>
/// </remarks>
public sealed class IdempotencyKeyInUseException(string key)
    : Exception($"Idempotency key '{key}' is claimed but has no recorded outcome yet.")
{
    public string Key { get; } = key;
}
