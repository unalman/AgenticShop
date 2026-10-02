namespace AgenticShop.Ordering.Domain;

/// <summary>
/// One product on an order, snapshotted at placement time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProductName"/> and <see cref="UnitPrice"/> are copies taken from Catalog when the
/// order was placed, not references resolved on read. A later catalog edit must not rewrite order
/// history — that is a standing project invariant, and it is why Ordering is allowed to hold data
/// it does not own.
/// </para>
/// <para>
/// A line is write-once: nothing in Phase 0 mutates it after placement, so it carries no
/// timestamps of its own. <see cref="Order.PlacedAtUtc"/> is the whole aggregate's clock.
/// </para>
/// </remarks>
public class OrderLine
{
    /// <summary>
    /// Mirrors Catalog's <c>Product.NameMaxLength</c>. Declared here rather than shared, because
    /// there is deliberately no shared assembly; the two are kept honest by the fact that a longer
    /// name would be rejected by Catalog before it could ever be snapshotted.
    /// </summary>
    public const int ProductNameMaxLength = 200;

    /// <summary>
    /// Mirrors Stock's <c>StockItem.MaxQuantity</c>, so a quantity Ordering accepts is one Stock
    /// can hold. Declared here for the same reason. If the two ever diverge, Stock answers the
    /// reserve with a 400 and the placement fails loudly rather than silently over-ordering.
    /// </summary>
    public const int MaxQuantity = 1_000_000;

    /// <summary>EF Core materialisation only.</summary>
    private OrderLine()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// The order this line belongs to. A plain <see cref="Guid"/> foreign key, legitimate because
    /// both tables are in Ordering's own database; what the boundary forbids is a key crossing
    /// services.
    /// </summary>
    public Guid OrderId { get; private set; }

    /// <summary>Immutable. The product lives in Catalog's database and is referenced by id.</summary>
    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    /// <summary>
    /// Computed, never persisted — a stored copy could disagree with its inputs. Both inputs are
    /// columns of this same row, so the value can never be read without them.
    /// </summary>
    public decimal LineTotal => NormalizePrice(UnitPrice * Quantity);

    public static OrderLine Create(
        Guid orderId,
        Guid productId,
        string productName,
        decimal unitPrice,
        int quantity)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(orderId));
        }

        if (productId == Guid.Empty)
        {
            throw new ArgumentException("Product id is required.", nameof(productId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(productName.Length, ProductNameMaxLength, nameof(productName));
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice, nameof(unitPrice));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity, nameof(quantity));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantity, MaxQuantity, nameof(quantity));

        return new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ProductId = productId,
            ProductName = productName.Trim(),
            Quantity = quantity,
            UnitPrice = NormalizePrice(unitPrice)
        };
    }

    /// <summary>
    /// The project's single money rule, stated where it is applied: <see cref="decimal"/>, two
    /// places, away from zero — the same rounding Catalog applies to a product's price, so a
    /// snapshotted price and its line total cannot disagree by a cent. There is deliberately no
    /// <c>Money</c> value object.
    /// </summary>
    private static decimal NormalizePrice(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
