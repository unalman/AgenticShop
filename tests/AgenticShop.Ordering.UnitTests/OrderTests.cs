using System.Globalization;
using AgenticShop.Ordering.Domain;
using FluentAssertions;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// The aggregate's invariants, plus the property the whole confirm-phase policy rests on: a
/// persisted order is always terminal, and its status says what actually shipped.
/// </summary>
public class OrderTests
{
    private static readonly Guid OrderId = Guid.NewGuid();

    private static OrderLine Line(
        Guid? productId = null,
        decimal unitPrice = 10.00m,
        int quantity = 1,
        string productName = "Widget",
        Guid? orderId = null)
        => OrderLine.Create(
            orderId ?? OrderId,
            productId ?? Guid.NewGuid(),
            productName,
            unitPrice,
            quantity);

    private static Order NewOrder() => Order.Create(OrderId, "USD", [Line()]);

    [Fact]
    public void Create_StartsPendingAndNotTerminal()
    {
        var order = NewOrder();

        order.Status.Should().Be(OrderStatus.Pending);
        order.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Create_KeepsTheIdTheCallerSupplied()
    {
        // Not generated internally: Stock's reservations carry the order id and are created before
        // the order row exists, so the id has to be known first.
        NewOrder().Id.Should().Be(OrderId);
    }

    [Fact]
    public void Create_DerivesTheOrderNumberFromTheIdAndTheDate()
    {
        var order = NewOrder();

        order.OrderNumber.Should().MatchRegex(@"^ORD-\d{8}-[0-9a-f]{8}$");
        order.OrderNumber.Should().EndWith(order.Id.ToString("N")[..8]);
        order.OrderNumber.Should().Contain(
            order.PlacedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheOrderNumberIsAPureFunctionOfTheIdAndTheDay()
    {
        // Which is what removes the need for a sequence: nothing has to be allocated, so nothing
        // has to be reset between tests and nothing can be skipped by a rolled-back transaction.
        var first = NewOrder();
        var second = Order.Create(OrderId, "USD", [Line()]);

        second.OrderNumber.Should().Be(first.OrderNumber);
    }

    [Fact]
    public void TwoOrdersPlacedOnTheSameDayGetDifferentNumbers()
    {
        var otherId = Guid.NewGuid();
        var other = Order.Create(otherId, "USD", [Line(orderId: otherId)]);

        NewOrder().OrderNumber.Should().NotBe(other.OrderNumber);
    }

    [Fact]
    public void Create_SumsTheRoundedLineTotals()
    {
        var order = Order.Create(OrderId, "USD",
        [
            Line(unitPrice: 1.005m, quantity: 3),
            Line(unitPrice: 2.50m, quantity: 2)
        ]);

        // 1.005 rounds away from zero to 1.01 before it is multiplied, so the line is 3.03 and not
        // 3.015. Rounding the price first is what keeps a snapshotted price and Catalog's stored
        // price identical to the cent.
        order.TotalAmount.Should().Be(3.03m + 5.00m);
    }

    [Theory]
    [InlineData("0.125", "0.13")]
    [InlineData("0.124", "0.12")]
    [InlineData("2.675", "2.68")]
    public void ALineTotalRoundsAwayFromZero(string unitPrice, string expected)
    {
        // AwayFromZero, not the banker's rounding decimal.Round defaults to. Parsed from a string
        // rather than passed as a double, because a double cannot hold 2.675 exactly and this test
        // is about the last cent.
        var line = Line(unitPrice: decimal.Parse(unitPrice, CultureInfo.InvariantCulture));

        line.LineTotal.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Create_RejectsAnEmptyId()
    {
        var act = () => Order.Create(Guid.Empty, "USD", [Line()]);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("USDD")]
    public void Create_RejectsACurrencyThatIsNotThreeCharacters(string? currency)
    {
        var act = () => Order.Create(OrderId, currency!, [Line()]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_NormalisesTheCurrencyItSnapshots()
    {
        Order.Create(OrderId, " usd ", [Line()]).Currency.Should().Be("USD");
    }

    [Fact]
    public void Create_RejectsNullLines()
    {
        var act = () => Order.Create(OrderId, "USD", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_RejectsAnOrderWithNoLines()
    {
        var act = () => Order.Create(OrderId, "USD", []);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_RejectsMoreLinesThanTheMaximum()
    {
        var lines = Enumerable.Range(0, Order.MaxLines + 1).Select(_ => Line()).ToList();

        var act = () => Order.Create(OrderId, "USD", lines);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_AcceptsExactlyTheMaximumNumberOfLines()
    {
        var lines = Enumerable.Range(0, Order.MaxLines).Select(_ => Line()).ToList();

        Order.Create(OrderId, "USD", lines).Lines.Should().HaveCount(Order.MaxLines);
    }

    [Fact]
    public void Create_RejectsTwoLinesForTheSameProduct()
    {
        // Stock's UNIQUE(order_id, stock_item_id) allows one hold per product per order, so the
        // placement path combines duplicates before they reach here. This guard is what turns
        // forgetting to combine into a loud failure rather than a refused reservation.
        var productId = Guid.NewGuid();

        var act = () => Order.Create(OrderId, "USD", [Line(productId), Line(productId)]);

        act.Should().Throw<ArgumentException>().WithMessage("*more than one line*");
    }

    [Fact]
    public void Create_RejectsALineThatBelongsToAnotherOrder()
    {
        var act = () => Order.Create(OrderId, "USD", [Line(orderId: Guid.NewGuid())]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsATotalThatWouldNotFitTheColumn()
    {
        // Left to PostgreSQL this is a 22003 that names no order; caught here it is a 400 raised
        // before anything is reserved.
        var act = () => Order.Create(OrderId, "USD", [Line(unitPrice: Order.MaxTotalAmount, quantity: 2)]);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_AcceptsATotalExactlyAtTheMaximum()
    {
        var order = Order.Create(OrderId, "USD", [Line(unitPrice: Order.MaxTotalAmount, quantity: 1)]);

        order.TotalAmount.Should().Be(Order.MaxTotalAmount);
    }

    [Fact]
    public void Confirm_SettlesTheOrderAsTerminal()
    {
        var order = NewOrder();

        order.Confirm();

        order.Status.Should().Be(OrderStatus.Confirmed);
        order.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Fail_SettlesTheOrderAsTerminal()
    {
        var order = NewOrder();

        order.Fail();

        order.Status.Should().Be(OrderStatus.Failed);
        order.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void MarkPartiallyConfirmed_SettlesTheOrderAsTerminal()
    {
        var order = NewOrder();

        order.MarkPartiallyConfirmed();

        order.Status.Should().Be(OrderStatus.PartiallyConfirmed);
        order.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void PendingIsTheOnlyNonTerminalStatus()
    {
        // So a row written by the single SaveChangesAsync is never still in flight, and nothing in
        // Phase 0 has to advance it later — there is no worker that could.
        foreach (var status in Enum.GetValues<OrderStatus>().Where(s => s != OrderStatus.Pending))
        {
            var order = NewOrder();

            Settle(order, status);

            order.IsTerminal.Should().BeTrue($"every status but Pending must be terminal");
        }
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.PartiallyConfirmed)]
    public void ASecondTransitionIsRefused(OrderStatus first)
    {
        var order = NewOrder();
        Settle(order, first);

        var act = () => order.Confirm();

        // An InvalidOperationException, so the handler's default arm answers 500. No caller input
        // can reach this, because Phase 0 exposes no route that mutates a persisted order, so it can
        // only mean this assembly transitioned twice — and blaming the client would hide the bug
        // inside their error budget.
        act.Should().Throw<InvalidOperationException>().WithMessage($"*already {first}*");
    }

    [Fact]
    public void SettlingDoesNotChangeTheSnapshotsOrTheTotal()
    {
        // An order is write-once: the status is the only thing that moves after construction, and it
        // moves before the row is ever written.
        var order = Order.Create(OrderId, "USD", [Line(unitPrice: 4.20m, quantity: 3)]);
        var before = (order.TotalAmount, order.PlacedAtUtc, order.OrderNumber, order.Lines.Count);

        order.Confirm();

        (order.TotalAmount, order.PlacedAtUtc, order.OrderNumber, order.Lines.Count).Should().Be(before);
    }

    private static void Settle(Order order, OrderStatus status)
    {
        switch (status)
        {
            case OrderStatus.Confirmed:
                order.Confirm();
                break;
            case OrderStatus.Failed:
                order.Fail();
                break;
            case OrderStatus.PartiallyConfirmed:
                order.MarkPartiallyConfirmed();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
    }
}
