namespace AgenticShop.Ordering.Clients;

/// <summary>
/// Ordering's own view of one of Stock's reservations.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is Stock's vocabulary, not Ordering's, and is deliberately kept a string
/// rather than mapped onto an enum here. Only one value is ever inspected —
/// <see cref="Statuses.Confirmed"/> — because that is the only one with an irreversible meaning.
/// Modelling the whole of another service's state machine inside this one would create a second
/// copy of its truth that could drift silently.
/// </remarks>
public sealed record StockReservationSnapshot(
    Guid Id,
    Guid ProductId,
    int Quantity,
    string Status)
{
    public static class Statuses
    {
        public const string Pending = "Pending";
        public const string Confirmed = "Confirmed";
        public const string Released = "Released";
    }

    public bool IsConfirmed => string.Equals(Status, Statuses.Confirmed, StringComparison.Ordinal);

    public bool IsPending => string.Equals(Status, Statuses.Pending, StringComparison.Ordinal);
}
