using AgenticShop.Ordering.Domain;
using FluentAssertions;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// The snapshot itself. A line is the only place in this service that holds data another service
/// owns, so its guards are what stop a bad catalog response from becoming permanent order history.
/// </summary>
public class OrderLineTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid ProductId = Guid.NewGuid();

    private static OrderLine NewLine(
        Guid? orderId = null,
        Guid? productId = null,
        string productName = "Widget",
        decimal unitPrice = 10.00m,
        int quantity = 1)
        => OrderLine.Create(
            orderId ?? OrderId,
            productId ?? ProductId,
            productName,
            unitPrice,
            quantity);

    [Fact]
    public void Create_SnapshotsEverythingItIsGiven()
    {
        var line = NewLine(productName: "  Blue Widget  ", unitPrice: 3.50m, quantity: 4);

        line.OrderId.Should().Be(OrderId);
        line.ProductId.Should().Be(ProductId);
        line.ProductName.Should().Be("Blue Widget");
        line.UnitPrice.Should().Be(3.50m);
        line.Quantity.Should().Be(4);
        line.LineTotal.Should().Be(14.00m);
    }

    [Fact]
    public void Create_RoundsTheSnapshottedPriceTheWayCatalogDoes()
    {
        // Catalog stores a price rounded to two places away from zero. Snapshotting anything else
        // would make the order disagree with the product it was priced from.
        NewLine(unitPrice: 1.005m).UnitPrice.Should().Be(1.01m);
        NewLine(unitPrice: 1.004m).UnitPrice.Should().Be(1.00m);
    }

    [Fact]
    public void Create_RejectsAnEmptyOrderId()
    {
        var act = () => NewLine(orderId: Guid.Empty);

        act.Should().Throw<ArgumentException>().WithMessage("*Order id*");
    }

    [Fact]
    public void Create_RejectsAnEmptyProductId()
    {
        var act = () => NewLine(productId: Guid.Empty);

        act.Should().Throw<ArgumentException>().WithMessage("*Product id*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsAProductWithoutAName(string? productName)
    {
        var act = () => NewLine(productName: productName!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsANameLongerThanTheSnapshottedColumn()
    {
        var act = () => NewLine(productName: new string('x', OrderLine.ProductNameMaxLength + 1));

        act.Should().Throw<ArgumentOutOfRangeException>();

        NewLine(productName: new string('x', OrderLine.ProductNameMaxLength))
            .ProductName.Should().HaveLength(OrderLine.ProductNameMaxLength, "the boundary itself is legal");
    }

    [Fact]
    public void Create_RejectsANegativePrice()
    {
        var act = () => NewLine(unitPrice: -0.01m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_AcceptsAFreeProduct()
    {
        NewLine(unitPrice: 0m).LineTotal.Should().Be(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsAQuantityThatIsNotPositive(int quantity)
    {
        var act = () => NewLine(quantity: quantity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_RejectsAQuantityBeyondTheBoundItSharesWithStock()
    {
        var act = () => NewLine(quantity: OrderLine.MaxQuantity + 1);

        act.Should().Throw<ArgumentOutOfRangeException>();

        NewLine(quantity: OrderLine.MaxQuantity).Quantity.Should().Be(OrderLine.MaxQuantity);
    }

    [Fact]
    public void ALineHasNoIdentityOrTimestampOfItsOwn()
    {
        // Write-once, and part of the order aggregate: Order.PlacedAtUtc is the only clock, so a
        // line cannot disagree with the order it belongs to about when it was placed.
        var line = NewLine();

        line.Id.Should().NotBe(Guid.Empty);

        typeof(OrderLine).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("AtUtc", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoLinesForDifferentProductsGetDifferentIds()
    {
        NewLine().Id.Should().NotBe(NewLine(productId: Guid.NewGuid()).Id);
    }
}
