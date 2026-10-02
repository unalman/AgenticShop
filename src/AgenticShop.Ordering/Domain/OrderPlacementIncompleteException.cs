namespace AgenticShop.Ordering.Domain;

/// <summary>
/// Thrown when placement reached a terminal state that is not <see cref="OrderStatus.Confirmed"/>
/// because a downstream service failed or timed out. The order row has been persisted by the time
/// this is raised; the status says what actually happened to it.
/// </summary>
/// <remarks>
/// <para>
/// A 502, never a 4xx: the caller's request was well-formed and a dependency let us down. The
/// handler composes the detail from <see cref="Status"/> rather than from
/// <see cref="Exception.Message"/>, and echoes <see cref="OrderId"/> as a ProblemDetails extension
/// — a 502 with no identifying body leaves the client unable to tell "nothing was created" from
/// "something was created and is broken", which is the ambiguity that causes blind retries.
/// </para>
/// <para>
/// <see cref="OrderStatus.PartiallyConfirmed"/> is reachable only from the confirm phase. A fault
/// during reservation cannot have shipped anything, so its worst case is a stranded hold and the
/// order is <see cref="OrderStatus.Failed"/> even when the reconciliation read also failed.
/// </para>
/// </remarks>
public sealed class OrderPlacementIncompleteException(Guid orderId, OrderStatus status)
    : Exception($"Order {orderId} ended placement as {status}.")
{
    public Guid OrderId { get; } = orderId;

    public OrderStatus Status { get; } = status;
}
