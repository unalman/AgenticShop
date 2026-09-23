using AgenticShop.Stock.Domain;
using FluentAssertions;

namespace AgenticShop.Stock.UnitTests;

/// <summary>
/// The lifecycle is <c>Pending → Confirmed | Released</c> and both targets are absorbing.
/// Transitions are strict rather than idempotent, so a retry of a successful confirm is a
/// rejected transition — which is the behaviour Ordering must learn to treat as success.
/// </summary>
public class StockReservationTests
{
    private static readonly Guid StockItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OrderId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static StockReservation Pending(int quantity = 3)
        => StockReservation.Create(StockItemId, OrderId, quantity);

    [Fact]
    public void Create_StartsPending()
    {
        var before = DateTimeOffset.UtcNow;

        var reservation = Pending(5);

        reservation.Id.Should().NotBeEmpty();
        reservation.StockItemId.Should().Be(StockItemId);
        reservation.OrderId.Should().Be(OrderId);
        reservation.Quantity.Should().Be(5);
        reservation.Status.Should().Be(ReservationStatus.Pending);
        reservation.IsPending.Should().BeTrue();
        reservation.CreatedAtUtc.Should().BeOnOrAfter(before);
        reservation.CreatedAtUtc.Should().Be(reservation.UpdatedAtUtc);
    }

    [Fact]
    public void Create_RejectsAnEmptyStockItemId()
    {
        var act = () => StockReservation.Create(Guid.Empty, OrderId, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsAnEmptyOrderId()
    {
        var act = () => StockReservation.Create(StockItemId, Guid.Empty, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsANonPositiveQuantity(int quantity)
    {
        var act = () => StockReservation.Create(StockItemId, OrderId, quantity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_RejectsAQuantityAboveTheStorableMaximum()
    {
        var act = () => StockReservation.Create(StockItemId, OrderId, StockItem.MaxQuantity + 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Confirm_MovesAPendingReservationToConfirmed()
    {
        var reservation = Pending();

        reservation.Confirm();

        reservation.Status.Should().Be(ReservationStatus.Confirmed);
        reservation.IsPending.Should().BeFalse();
    }

    [Fact]
    public void Release_MovesAPendingReservationToReleased()
    {
        var reservation = Pending();

        reservation.Release();

        reservation.Status.Should().Be(ReservationStatus.Released);
        reservation.IsPending.Should().BeFalse();
    }

    [Fact]
    public void ATransitionAdvancesTheTimestampButNotTheCreationTime()
    {
        var reservation = Pending();
        var created = reservation.CreatedAtUtc;

        reservation.Confirm();

        reservation.CreatedAtUtc.Should().Be(created);
        reservation.UpdatedAtUtc.Should().BeOnOrAfter(created);
    }

    [Fact]
    public void ConfirmingTwiceIsRejected()
    {
        var reservation = Pending();
        reservation.Confirm();

        var act = () => reservation.Confirm();

        act.Should().Throw<InvalidReservationStateException>();
    }

    [Fact]
    public void ReleasingTwiceIsRejected()
    {
        var reservation = Pending();
        reservation.Release();

        var act = () => reservation.Release();

        act.Should().Throw<InvalidReservationStateException>();
    }

    [Fact]
    public void ConfirmingAReleasedReservationIsRejected()
    {
        var reservation = Pending();
        reservation.Release();

        var act = () => reservation.Confirm();

        act.Should().Throw<InvalidReservationStateException>();
    }

    [Fact]
    public void ReleasingAConfirmedReservationIsRejected()
    {
        var reservation = Pending();
        reservation.Confirm();

        var act = () => reservation.Release();

        act.Should().Throw<InvalidReservationStateException>();
    }

    [Theory]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Released)]
    [InlineData(ReservationStatus.Released, ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.Released, ReservationStatus.Released)]
    public void AnIllegalTransitionCarriesBothStatesSoTheHandlerCanExplainIt(
        ReservationStatus expectedCurrent,
        ReservationStatus expectedAttempted)
    {
        var reservation = Pending();

        if (expectedCurrent == ReservationStatus.Confirmed)
        {
            reservation.Confirm();
        }
        else
        {
            reservation.Release();
        }

        var act = () =>
        {
            if (expectedAttempted == ReservationStatus.Confirmed)
            {
                reservation.Confirm();
            }
            else
            {
                reservation.Release();
            }
        };

        var exception = act.Should().Throw<InvalidReservationStateException>().And;

        // The handler builds the client message from these, never from Message, so they must
        // be accurate — a wrong pair would tell the caller the opposite of what happened.
        exception.CurrentStatus.Should().Be(expectedCurrent);
        exception.AttemptedStatus.Should().Be(expectedAttempted);
    }

    [Fact]
    public void AFailedTransitionLeavesTheReservationUnchanged()
    {
        var reservation = Pending();
        reservation.Confirm();
        var updated = reservation.UpdatedAtUtc;

        var act = () => reservation.Release();

        act.Should().Throw<InvalidReservationStateException>();

        reservation.Status.Should().Be(ReservationStatus.Confirmed);
        reservation.UpdatedAtUtc.Should().Be(updated);
    }

    [Fact]
    public void QuantityCannotBeChangedAfterCreation()
    {
        // The stock counters were moved by this exact amount when the hold was taken, so a
        // mutable quantity would let the settlement diverge from the reservation.
        var setter = typeof(StockReservation)
            .GetProperty(nameof(StockReservation.Quantity))!
            .GetSetMethod(nonPublic: true);

        setter.Should().NotBeNull("EF Core needs a setter to materialise the entity");
        setter!.IsPrivate.Should().BeTrue("but it must not be publicly assignable");
    }
}
