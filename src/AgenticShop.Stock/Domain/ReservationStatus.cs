namespace AgenticShop.Stock.Domain;

/// <summary>
/// Persisted as a string with a CHECK constraint, so reservation state is readable directly
/// in psql. That matters once Phase 2 adds sagas and reconciliation.
/// </summary>
public enum ReservationStatus
{
    /// <summary>Quantity is held against <see cref="StockItem.Reserved"/> but not yet shipped.</summary>
    Pending = 0,

    /// <summary>Terminal. The goods have left: on-hand and reserved both dropped.</summary>
    Confirmed = 1,

    /// <summary>Terminal. The hold was lifted; on-hand was never touched.</summary>
    Released = 2
}
