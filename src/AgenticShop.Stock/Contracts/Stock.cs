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

/// <summary>Holds quantity against an order.</summary>
public sealed record ReserveStockRequest(
    Guid OrderId,

    [property: Range(1, StockItem.MaxQuantity)]
    int Quantity) : IRequestContract;

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

public sealed record ReservationResponse(
    Guid Id,
    Guid StockItemId,
    Guid ProductId,
    Guid OrderId,
    int Quantity,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>
    /// <paramref name="productId"/> is supplied by the caller rather than stored on the
    /// reservation: the row keys off <c>stock_item_id</c>, and duplicating the product id would
    /// create a second copy of the truth inside this database.
    /// </summary>
    public static ReservationResponse From(StockReservation reservation, Guid productId) => new(
        reservation.Id,
        reservation.StockItemId,
        productId,
        reservation.OrderId,
        reservation.Quantity,
        reservation.Status.ToString(),
        reservation.CreatedAtUtc,
        reservation.UpdatedAtUtc);
}
