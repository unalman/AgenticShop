using System.ComponentModel.DataAnnotations;
using AgenticShop.Shared.Contracts;
using AgenticShop.Stock.Domain;

namespace AgenticShop.Stock.Contracts;

/// <summary>Provisions the stock record for a product.</summary>
/// <remarks>
/// An empty <see cref="ProductId"/> is rejected by the domain rather than by an attribute:
/// there is no DataAnnotation that expresses "non-default Guid", and inventing one would be a
/// new abstraction for a single field.
/// </remarks>
public sealed record SetStockRequest(
    Guid ProductId,

    [property: Range(0, StockItem.MaxQuantity)]
    int QuantityOnHand) : IRequestContract;

/// <summary>Replaces the physical count. Cannot go below what is already reserved.</summary>
public sealed record UpdateStockRequest(
    [property: Range(0, StockItem.MaxQuantity)]
    int QuantityOnHand) : IRequestContract;

public sealed record StockResponse(
    Guid Id,
    Guid ProductId,
    int QuantityOnHand,
    int Reserved,
    int Available,
    DateTimeOffset UpdatedAtUtc)
{
    public static StockResponse From(StockItem item) => new(
        item.Id,
        item.ProductId,
        item.QuantityOnHand,
        item.Reserved,
        item.Available,
        item.UpdatedAtUtc);
}
