using AgenticShop.Ordering.Clients;

namespace AgenticShop.Ordering.IntegrationTests.Fakes;

/// <summary>
/// Stands in for Stock at the interface seam, and keeps real reservation state so that
/// <see cref="ListByOrderAsync"/> reports what the previous calls actually did rather than what a
/// test remembered to script.
/// </summary>
/// <remarks>
/// <para>
/// That matters because the confirm-phase policy is decided by the read. A fake whose reconciliation
/// response was scripted independently of its confirm behaviour could agree with the policy by
/// construction and prove nothing. Here, a confirm that is told to apply-then-lose really does
/// mutate the state the read returns, so "all confirmed → 201" is earned rather than asserted.
/// </para>
/// <para>
/// The state machine mirrors Stock's: only a <c>Pending</c> reservation settles, and settling an
/// already-settled one is refused. That is what produces the ambiguous 409 Ordering must not guess
/// at.
/// </para>
/// </remarks>
public sealed class FakeStockClient : IStockClient
{
    /// <summary>How an operation goes wrong, and what the caller can tell from the outside.</summary>
    public enum Failure
    {
        None,

        /// <summary>A 404 or 409: refused, cleanly, with nothing changed.</summary>
        Refused,

        /// <summary>A 5xx or a timeout: the caller cannot know whether it applied.</summary>
        Fault,

        /// <summary>
        /// It applied, and then the response was lost. Indistinguishable from
        /// <see cref="Fault"/> to the caller, which is exactly why the reconciliation read exists.
        /// </summary>
        AppliedThenLost
    }

    public const string ReserveOperation = "reserve";
    public const string ConfirmOperation = "confirm";
    public const string ReleaseOperation = "release";
    public const string ListOperation = "list";

    private readonly List<Reservation> _reservations = [];
    private int _reserveCalls;
    private int _confirmCalls;

    public Failure ReserveFailsWith { get; set; }

    /// <summary>Zero-based index of the first reserve call that fails; earlier ones succeed.</summary>
    public int ReserveFailsFromCall { get; set; }

    public Failure ConfirmFailsWith { get; set; }

    /// <summary>Zero-based index of the first confirm call that fails; earlier ones succeed.</summary>
    public int ConfirmFailsFromCall { get; set; }

    public bool ReleasesFail { get; set; }

    public bool ListFails { get; set; }

    /// <summary>Every call in order, so "never released the confirmed one" is a direct assertion.</summary>
    public List<Call> Calls { get; } = [];

    public IReadOnlyList<Reservation> Reservations => _reservations;

    public IEnumerable<Guid> IdsPassedTo(string operation)
        => Calls.Where(call => call.Operation == operation).Select(call => call.Id);

    public int CountCalls(string operation) => Calls.Count(call => call.Operation == operation);

    public IEnumerable<Guid> ReservationIdsInStatus(string status)
        => _reservations.Where(r => r.Status == status).Select(r => r.Id);

    public void Reset()
    {
        _reservations.Clear();
        Calls.Clear();
        _reserveCalls = 0;
        _confirmCalls = 0;
        ReserveFailsWith = Failure.None;
        ReserveFailsFromCall = 0;
        ConfirmFailsWith = Failure.None;
        ConfirmFailsFromCall = 0;
        ReleasesFail = false;
        ListFails = false;
    }

    public Task<Guid?> ReserveAsync(
        Guid productId,
        Guid orderId,
        int quantity,
        CancellationToken cancellationToken)
    {
        var call = _reserveCalls++;
        Calls.Add(new Call(ReserveOperation, productId));

        var failure = Scripted(ReserveFailsWith, call, ReserveFailsFromCall);

        // AppliedThenLost still takes the hold: the reservation exists in Stock and only the 201
        // carrying its id was lost, which is precisely the case a caller cannot recover from without
        // reading the state back.
        var reservation = failure is Failure.None or Failure.AppliedThenLost
            ? Hold(orderId, productId, quantity)
            : null;

        switch (failure)
        {
            case Failure.Fault or Failure.AppliedThenLost:
                throw Unavailable(ReserveOperation);

            case Failure.Refused:
                return Task.FromResult<Guid?>(null);

            default:
                return Task.FromResult<Guid?>(reservation!.Id);
        }
    }

    public Task<bool> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var call = _confirmCalls++;
        Calls.Add(new Call(ConfirmOperation, reservationId));

        var reservation = Find(reservationId);
        var failure = Scripted(ConfirmFailsWith, call, ConfirmFailsFromCall);

        if (failure == Failure.AppliedThenLost)
        {
            reservation.Status = StockReservationSnapshot.Statuses.Confirmed;
            throw Unavailable(ConfirmOperation);
        }

        if (failure == Failure.Fault)
        {
            throw Unavailable(ConfirmOperation);
        }

        if (failure == Failure.Refused)
        {
            return Task.FromResult(false);
        }

        // Stock's transitions are strict, not idempotent: an already-settled reservation is refused.
        if (reservation.Status != StockReservationSnapshot.Statuses.Pending)
        {
            return Task.FromResult(false);
        }

        reservation.Status = StockReservationSnapshot.Statuses.Confirmed;

        return Task.FromResult(true);
    }

    public Task<bool> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(ReleaseOperation, reservationId));

        if (ReleasesFail)
        {
            throw Unavailable(ReleaseOperation);
        }

        var reservation = Find(reservationId);

        if (reservation.Status != StockReservationSnapshot.Statuses.Pending)
        {
            return Task.FromResult(false);
        }

        reservation.Status = StockReservationSnapshot.Statuses.Released;

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<StockReservationSnapshot>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        Calls.Add(new Call(ListOperation, orderId));

        if (ListFails)
        {
            throw Unavailable(ListOperation);
        }

        return Task.FromResult<IReadOnlyList<StockReservationSnapshot>>(
            _reservations
                .Where(reservation => reservation.OrderId == orderId)
                .Select(reservation => new StockReservationSnapshot(
                    reservation.Id,
                    reservation.ProductId,
                    reservation.Quantity,
                    reservation.Status))
                .ToList());
    }

    private Reservation Hold(Guid orderId, Guid productId, int quantity)
    {
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ProductId = productId,
            Quantity = quantity
        };

        _reservations.Add(reservation);

        return reservation;
    }

    private Reservation Find(Guid reservationId)
        => _reservations.SingleOrDefault(r => r.Id == reservationId)
           ?? throw new InvalidOperationException(
               $"The test asked Stock about reservation {reservationId}, which was never created.");

    private static Failure Scripted(Failure configured, int callIndex, int fromCall)
        => configured != Failure.None && callIndex >= fromCall ? configured : Failure.None;

    private static DownstreamServiceException Unavailable(string operation)
        => new("Stock", operation, statusCode: 503, innerException: null);

    public sealed class Reservation
    {
        public Guid Id { get; init; }

        public Guid OrderId { get; init; }

        public Guid ProductId { get; init; }

        public int Quantity { get; init; }

        public string Status { get; set; } = StockReservationSnapshot.Statuses.Pending;
    }

    public sealed record Call(string Operation, Guid Id);
}
