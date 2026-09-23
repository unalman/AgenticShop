namespace AgenticShop.Stock.Domain;

/// <summary>
/// Thrown when a reservation is asked to move to a state it cannot legally reach — for
/// example confirming something already released. Both terminal states are absorbing, so a
/// retry of a successful confirm lands here and is reported as 409.
/// </summary>
public sealed class InvalidReservationStateException(
    ReservationStatus currentStatus,
    ReservationStatus attemptedStatus)
    : Exception($"Reservation is {currentStatus}; cannot move it to {attemptedStatus}.")
{
    public ReservationStatus CurrentStatus { get; } = currentStatus;

    public ReservationStatus AttemptedStatus { get; } = attemptedStatus;
}
