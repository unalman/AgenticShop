using System.ComponentModel.DataAnnotations;
using AgenticShop.Ordering.Domain;
using AgenticShop.Shared.Contracts;

namespace AgenticShop.Ordering.Contracts;

/// <summary>
/// Places an order. The first genuinely nested request DTO in the repository, and therefore the
/// first real exercise of the validation filter's cascade — until now the recursion has been proven
/// only by synthetic contracts.
/// </summary>
/// <remarks>
/// Duplicate product lines are combined by the placement path rather than rejected: Stock's
/// <c>UNIQUE(order_id, stock_item_id)</c> allows one hold per product per order, so the second line
/// for a product would be refused. Combining is lossless because the unit price comes from Catalog
/// and is therefore identical for both.
/// </remarks>
public sealed record CreateOrderRequest(
    [property: Required]
    [property: MinLength(1, ErrorMessage = "An order needs at least one line.")]
    [property: MaxLength(Order.MaxLines)]
    IReadOnlyList<CreateOrderLineRequest> Lines) : IRequestContract;

/// <summary>
/// One requested line. Carries no price: the unit price is snapshotted from Catalog during
/// placement, so a client cannot name one.
/// </summary>
public sealed record CreateOrderLineRequest(
    Guid ProductId,

    [property: Range(1, OrderLine.MaxQuantity)]
    int Quantity) : IRequestContract;

public sealed record OrderResponse(
    Guid Id,
    string OrderNumber,
    string Status,
    string Currency,
    decimal TotalAmount,
    DateTimeOffset PlacedAtUtc,
    IReadOnlyList<OrderLineResponse> Lines)
{
    /// <summary>
    /// Lines are ordered rather than returned in whatever order the query produced. An
    /// <c>Include</c> carries no ordering guarantee, and a response whose lines shuffle between two
    /// reads of the same order is harder to test and harder to read than one that does not.
    /// </summary>
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.OrderNumber,
        order.Status.ToString(),
        order.Currency,
        order.TotalAmount,
        order.PlacedAtUtc,
        order.Lines
            .OrderBy(line => line.ProductName, StringComparer.Ordinal)
            .ThenBy(line => line.ProductId)
            .Select(OrderLineResponse.From)
            .ToList());
}

public sealed record OrderLineResponse(
    Guid Id,
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal)
{
    public static OrderLineResponse From(OrderLine line) => new(
        line.Id,
        line.ProductId,
        line.ProductName,
        line.Quantity,
        line.UnitPrice,
        line.LineTotal);
}
