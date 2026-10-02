namespace AgenticShop.Ordering.Domain;

/// <summary>
/// Thrown when Stock refused to hold a line, so the order cannot be fulfilled.
/// </summary>
/// <remarks>
/// <para>
/// Stock's 404 (no stock record for that product) and its two 409s (not enough available, or that
/// order already holds that product) all land here. They are deliberately not distinguished:
/// telling them apart would mean parsing Stock's <c>detail</c> text, and the shared error contract
/// says a caller must build its own vocabulary from typed properties instead. The second 409
/// cannot occur anyway — Ordering combines duplicate product lines and mints a fresh order id per
/// placement, so a duplicate hold would be a bug in this assembly.
/// </para>
/// <para>
/// Carries the order id so the handler can return it: the order row is persisted as
/// <see cref="OrderStatus.Failed"/>, and a client that cannot see the id cannot query it.
/// </para>
/// </remarks>
public sealed class StockUnavailableException(Guid productId, Guid orderId)
    : Exception($"Stock for product {productId} could not be reserved for order {orderId}.")
{
    public Guid ProductId { get; } = productId;

    public Guid OrderId { get; } = orderId;
}
