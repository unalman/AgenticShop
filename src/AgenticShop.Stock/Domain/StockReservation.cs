namespace AgenticShop.Stock.Domain;

/// <summary>
/// A hold against a <see cref="StockItem"/> on behalf of an order.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle is <c>Pending → Confirmed | Released</c>, and both targets are terminal.
/// Transitions are strict: confirming an already-settled reservation throws rather than
/// succeeding quietly, because a silent no-op would hide a double-settle from the caller.
/// </para>
/// <para>
/// That strictness has a known cost. Confirm and release are not idempotent, so a retry after
/// a successful confirm gets a 409. Phase 2's at-least-once delivery will require idempotency —
/// that is what Phase 1's idempotency keys and the Inbox exist for. Until then Ordering must
/// treat "409 because already confirmed" as success.
/// </para>
/// <para>
/// There is deliberately no navigation property to <see cref="StockItem"/>: the database
/// foreign key documents the relationship, and keeping the object graph flat means there is no
/// cascade behaviour to reason about. Endpoints load both entities explicitly.
/// </para>
/// </remarks>
public class StockReservation
{
    /// <summary>EF Core materialisation only.</summary>
    private StockReservation()
    {
    }

    public Guid Id { get; private set; }

    public Guid StockItemId { get; private set; }

    /// <summary>
    /// The caller's order. Stock never resolves it — that is Ordering's data, in Ordering's
    /// database.
    /// </summary>
    public Guid OrderId { get; private set; }

    public int Quantity { get; private set; }

    public ReservationStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool IsPending => Status == ReservationStatus.Pending;

    public static StockReservation Create(Guid stockItemId, Guid orderId, int quantity)
    {
        if (stockItemId == Guid.Empty)
        {
            throw new ArgumentException("Stock item id is required.", nameof(stockItemId));
        }

        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(orderId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantity, StockItem.MaxQuantity);

        var now = DateTimeOffset.UtcNow;

        return new StockReservation
        {
            Id = Guid.NewGuid(),
            StockItemId = stockItemId,
            OrderId = orderId,
            Quantity = quantity,
            Status = ReservationStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    /// <summary>Settles the hold as shipped.</summary>
    public void Confirm() => TransitionTo(ReservationStatus.Confirmed);

    /// <summary>Settles the hold as cancelled.</summary>
    public void Release() => TransitionTo(ReservationStatus.Released);

    private void TransitionTo(ReservationStatus target)
    {
        if (Status != ReservationStatus.Pending)
        {
            throw new InvalidReservationStateException(Status, target);
        }

        Status = target;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
