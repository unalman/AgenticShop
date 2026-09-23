using AgenticShop.Stock.Domain;
using FluentAssertions;

namespace AgenticShop.Stock.UnitTests;

/// <summary>
/// The invariant that matters is <c>0 &lt;= Reserved &lt;= QuantityOnHand</c>, which is what
/// makes <see cref="StockItem.Available"/> unable to go negative. Every test here either
/// establishes that invariant or tries to break it.
/// </summary>
public class StockItemTests
{
    private static readonly Guid ProductId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Create_StartsWithNothingReserved()
    {
        var before = DateTimeOffset.UtcNow;

        var item = StockItem.Create(ProductId, 10);

        item.Id.Should().NotBeEmpty();
        item.ProductId.Should().Be(ProductId);
        item.QuantityOnHand.Should().Be(10);
        item.Reserved.Should().Be(0);
        item.Available.Should().Be(10);
        item.UpdatedAtUtc.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Create_RejectsAnEmptyProductId()
    {
        var act = () => StockItem.Create(Guid.Empty, 10);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_RejectsANegativeCount(int quantity)
    {
        var act = () => StockItem.Create(ProductId, quantity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_RejectsACountAboveTheStorableMaximum()
    {
        var act = () => StockItem.Create(ProductId, StockItem.MaxQuantity + 1);

        // The bound exists so the counters cannot overflow int during Reserve/Confirm.
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reserve_HoldsQuantityWithoutTouchingThePhysicalCount()
    {
        var item = StockItem.Create(ProductId, 10);

        item.Reserve(3);

        item.Reserved.Should().Be(3);
        item.QuantityOnHand.Should().Be(10, "reserving holds stock, it does not ship it");
        item.Available.Should().Be(7);
    }

    [Fact]
    public void Reserve_AcceptsExactlyWhatIsAvailable()
    {
        var item = StockItem.Create(ProductId, 10);

        item.Reserve(10);

        item.Reserved.Should().Be(10);
        item.Available.Should().Be(0);
    }

    [Fact]
    public void Reserve_RejectsOneUnitMoreThanIsAvailable()
    {
        var item = StockItem.Create(ProductId, 10);

        var act = () => item.Reserve(11);

        act.Should().Throw<InsufficientStockException>()
            .Which.Available.Should().Be(10);
    }

    [Fact]
    public void Reserve_ReportsBothNumbersSoTheHandlerCanExplainTheRejection()
    {
        var item = StockItem.Create(ProductId, 4);

        var exception = Record.Exception(() => item.Reserve(9)).As<InsufficientStockException>();

        exception.Available.Should().Be(4);
        exception.Requested.Should().Be(9);
    }

    [Fact]
    public void Reserve_RejectsAnythingWhenNothingIsAvailable()
    {
        var item = StockItem.Create(ProductId, 0);

        var act = () => item.Reserve(1);

        act.Should().Throw<InsufficientStockException>();
    }

    [Fact]
    public void Reserve_AccumulatesAcrossSeveralOrders()
    {
        var item = StockItem.Create(ProductId, 10);

        item.Reserve(3);
        item.Reserve(4);

        item.Reserved.Should().Be(7);
        item.Available.Should().Be(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_RejectsANonPositiveQuantity(int quantity)
    {
        var item = StockItem.Create(ProductId, 10);

        var act = () => item.Reserve(quantity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConfirmReservation_ShipsTheGoodsSoBothCountersDrop()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(4);

        item.ConfirmReservation(4);

        item.QuantityOnHand.Should().Be(6);
        item.Reserved.Should().Be(0);
    }

    [Fact]
    public void ConfirmReservation_LeavesAvailabilityUnchanged()
    {
        // Available already fell when the hold was taken; confirming must not drop it twice,
        // or the same unit would be sold to two orders.
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(4);

        var before = item.Available;
        item.ConfirmReservation(4);

        item.Available.Should().Be(before).And.Be(6);
    }

    [Fact]
    public void ConfirmReservation_PartiallySettlesAndKeepsTheRemainderHeld()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(4);

        item.ConfirmReservation(1);

        item.QuantityOnHand.Should().Be(9);
        item.Reserved.Should().Be(3);
        item.Available.Should().Be(6);
    }

    [Fact]
    public void ConfirmReservation_RejectsMoreThanIsHeld()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(2);

        // Reachable only if the counters have diverged from the reservation rows, so this is a
        // data-integrity fault rather than a caller error.
        var act = () => item.ConfirmReservation(3);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ReleaseReservation_LiftsTheHoldWithoutShippingAnything()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(4);

        item.ReleaseReservation(4);

        item.QuantityOnHand.Should().Be(10, "nothing was shipped");
        item.Reserved.Should().Be(0);
        item.Available.Should().Be(10, "the hold is lifted so the stock is sellable again");
    }

    [Fact]
    public void ReleaseReservation_RejectsMoreThanIsHeld()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(2);

        var act = () => item.ReleaseReservation(3);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetQuantityOnHand_ReplacesThePhysicalCount()
    {
        var item = StockItem.Create(ProductId, 10);

        item.SetQuantityOnHand(25);

        item.QuantityOnHand.Should().Be(25);
        item.Available.Should().Be(25);
    }

    [Fact]
    public void SetQuantityOnHand_CannotDropBelowWhatIsAlreadyHeld()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(6);

        var act = () => item.SetQuantityOnHand(5);

        // Otherwise the in-flight reservations would no longer be coverable and the
        // reserved-within-on-hand invariant would break.
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SetQuantityOnHand_CanDropToExactlyWhatIsHeld()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(6);

        item.SetQuantityOnHand(6);

        item.QuantityOnHand.Should().Be(6);
        item.Available.Should().Be(0);
    }

    [Fact]
    public void AFailingReserveLeavesTheCountersUntouched()
    {
        // Guards must run before any mutation, or a rejected call would leave a partial change
        // that EF persists on the next save.
        var item = StockItem.Create(ProductId, 3);
        var updated = item.UpdatedAtUtc;

        var act = () => item.Reserve(9);

        act.Should().Throw<InsufficientStockException>();

        item.Reserved.Should().Be(0);
        item.QuantityOnHand.Should().Be(3);
        item.Available.Should().Be(3);
        item.UpdatedAtUtc.Should().Be(updated);
    }

    [Fact]
    public void AFailingSetQuantityOnHandLeavesTheCountersUntouched()
    {
        var item = StockItem.Create(ProductId, 10);
        item.Reserve(6);
        var updated = item.UpdatedAtUtc;

        var act = () => item.SetQuantityOnHand(1);

        act.Should().Throw<ArgumentException>();

        item.QuantityOnHand.Should().Be(10);
        item.UpdatedAtUtc.Should().Be(updated);
    }

    [Fact]
    public void EveryMutationAdvancesTheTimestamp()
    {
        var item = StockItem.Create(ProductId, 10);
        var created = item.UpdatedAtUtc;

        item.Reserve(2);
        var afterReserve = item.UpdatedAtUtc;
        afterReserve.Should().BeOnOrAfter(created);

        item.ConfirmReservation(2);
        item.UpdatedAtUtc.Should().BeOnOrAfter(afterReserve);
    }

    [Fact]
    public void AFullReserveConfirmReleaseCycleNeverBreaksTheInvariant()
    {
        // Walks the lifecycle and asserts the invariant after every step, rather than only at
        // the end where a transient breach would be invisible.
        var item = StockItem.Create(ProductId, 10);

        void AssertInvariant(string because)
        {
            item.Reserved.Should().BeGreaterThanOrEqualTo(0, because);
            item.QuantityOnHand.Should().BeGreaterThanOrEqualTo(0, because);
            item.Reserved.Should().BeLessThanOrEqualTo(item.QuantityOnHand, because);
            item.Available.Should().BeGreaterThanOrEqualTo(0, because);
        }

        AssertInvariant("after create");

        item.Reserve(6);
        AssertInvariant("after reserving 6");

        item.Reserve(4);
        AssertInvariant("after reserving the remainder");

        item.ConfirmReservation(6);
        AssertInvariant("after confirming 6");

        item.ReleaseReservation(4);
        AssertInvariant("after releasing 4");

        item.QuantityOnHand.Should().Be(4);
        item.Reserved.Should().Be(0);
        item.Available.Should().Be(4);
    }
}
