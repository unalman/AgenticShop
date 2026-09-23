namespace AgenticShop.Stock.Domain;

/// <summary>
/// The inventory counter for one product. Stock owns this row exclusively; <see cref="ProductId"/>
/// is a plain <see cref="Guid"/> because the product lives in Catalog's database, and a
/// cross-service foreign key is exactly what the boundary forbids.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Reserved"/> is a stored column rather than a SUM over <c>stock_reservations</c>.
/// That keeps the availability check and the mutation a single-row operation guarded by one
/// xmin token; deriving it would need an aggregate read plus a write in one transaction, and
/// the token would no longer cover reservation changes. The reservation table is the audit
/// trail of intent — this is the counter that makes the check atomic. Both are written by the
/// same <c>SaveChangesAsync</c>, which is the property Phase 2's outbox will rely on.
/// </para>
/// <para>
/// The invariant <c>0 &lt;= Reserved &lt;= QuantityOnHand</c> holds after every method here, so
/// <see cref="Available"/> can never be negative. It is also enforced by CHECK constraints,
/// which can only fire if this class has a bug.
/// </para>
/// </remarks>
public class StockItem
{
    /// <summary>
    /// Bounds every quantity so the counters cannot overflow <see cref="int"/>. Shared with
    /// the request contracts, which assert the same range.
    /// </summary>
    public const int MaxQuantity = 1_000_000;

    /// <summary>EF Core materialisation only.</summary>
    private StockItem()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// Immutable once created: Ordering holds this value, and it is the unique key that makes
    /// one stock row per product.
    /// </summary>
    public Guid ProductId { get; private set; }

    public int QuantityOnHand { get; private set; }

    public int Reserved { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Computed, never persisted — a stored copy could disagree with its inputs.</summary>
    public int Available => QuantityOnHand - Reserved;

    public static StockItem Create(Guid productId, int quantityOnHand)
    {
        if (productId == Guid.Empty)
        {
            throw new ArgumentException("Product id is required.", nameof(productId));
        }

        ValidateStorableQuantity(quantityOnHand, nameof(quantityOnHand));

        return new StockItem
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            QuantityOnHand = quantityOnHand,
            Reserved = 0,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Sets the physical count. It cannot be lowered below what is already held, or the
    /// reservations in flight would no longer be coverable.
    /// </summary>
    public void SetQuantityOnHand(int quantityOnHand)
    {
        ValidateStorableQuantity(quantityOnHand, nameof(quantityOnHand));

        if (quantityOnHand < Reserved)
        {
            throw new ArgumentException(
                $"Cannot set on-hand to {quantityOnHand} while {Reserved} unit(s) are reserved.",
                nameof(quantityOnHand));
        }

        QuantityOnHand = quantityOnHand;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Holds quantity for an order. Throws <see cref="InsufficientStockException"/> rather than
    /// clamping, so the caller learns the reservation was not made.
    /// </summary>
    public void Reserve(int quantity)
    {
        ValidatePositive(quantity);

        if (quantity > Available)
        {
            throw new InsufficientStockException(Available, quantity);
        }

        Reserved += quantity;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Completes a hold: the goods leave, so on-hand and reserved both drop and
    /// <see cref="Available"/> is unchanged — it already fell when the hold was taken.
    /// </summary>
    public void ConfirmReservation(int quantity)
    {
        ValidatePositive(quantity);
        RequireCoveredByReserved(quantity);

        QuantityOnHand -= quantity;
        Reserved -= quantity;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Lifts a hold without shipping anything, so on-hand is untouched.</summary>
    public void ReleaseReservation(int quantity)
    {
        ValidatePositive(quantity);
        RequireCoveredByReserved(quantity);

        Reserved -= quantity;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Every quantity reaches this class from a persisted reservation whose creation
    /// incremented <see cref="Reserved"/> by exactly that amount, and the state machine allows
    /// each reservation to settle once. Reaching this throw therefore means the counters have
    /// diverged from the reservation rows — a data-integrity fault, reported as a 500 rather
    /// than blamed on the caller.
    /// </summary>
    private void RequireCoveredByReserved(int quantity)
    {
        if (quantity > Reserved)
        {
            throw new InvalidOperationException(
                $"Cannot settle {quantity} unit(s): only {Reserved} are reserved. " +
                "The stock counters have diverged from the reservation rows.");
        }
    }

    private static void ValidatePositive(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantity, MaxQuantity);
    }

    private static void ValidateStorableQuantity(int quantity, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity, paramName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantity, MaxQuantity, paramName);
    }
}
