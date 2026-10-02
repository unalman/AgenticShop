namespace AgenticShop.Ordering.Clients;

/// <summary>
/// The seam over Stock: hold, settle, and read back what actually happened.
/// </summary>
/// <remarks>
/// <para>
/// Each method reports only what the caller can act on, and never parses Stock's
/// <c>ProblemDetails.detail</c> — the shared error contract says a caller builds its own vocabulary
/// from typed properties, not from another service's prose. So a 404 and a 409 collapse into the
/// same "refused" answer, and the two 409s Stock can return on confirm collapse into one too.
/// </para>
/// <para>
/// That collapse is safe only because of <see cref="ListByOrderAsync"/>. A confirm that answers 409
/// may mean "already confirmed", which decision D3 says must be treated as success, or "xmin
/// conflict", which must not. Rather than guess from a status code, the placement path reads the
/// authoritative state back.
/// </para>
/// </remarks>
public interface IStockClient
{
    /// <summary>
    /// Holds quantity for an order. Returns the reservation id, or <see langword="null"/> when
    /// Stock refused — there is no stock record for the product, or not enough is available.
    /// </summary>
    /// <exception cref="DownstreamServiceException">Stock returned a 5xx, timed out, or was unreachable.</exception>
    Task<Guid?> ReserveAsync(Guid productId, Guid orderId, int quantity, CancellationToken cancellationToken);

    /// <summary>
    /// Settles a hold as shipped. Returns <see langword="false"/> when Stock refused, which
    /// includes "already confirmed" — the caller must reconcile rather than assume.
    /// </summary>
    /// <exception cref="DownstreamServiceException">Stock returned a 5xx, timed out, or was unreachable.</exception>
    Task<bool> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Lifts a hold without shipping anything. Returns <see langword="false"/> when Stock refused,
    /// which includes "already released" and an xmin conflict; the two are indistinguishable from
    /// the status code alone.
    /// </summary>
    /// <exception cref="DownstreamServiceException">Stock returned a 5xx, timed out, or was unreachable.</exception>
    Task<bool> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every reservation Stock holds for one order — the reconciliation path. Authoritative,
    /// and the only way to resolve an ambiguous confirm.
    /// </summary>
    /// <remarks>
    /// An unlocked point-in-time read. A release issued on the strength of it can still be refused
    /// by xmin, so callers treat a refused release as "log and carry on", never as a hard failure.
    /// </remarks>
    /// <exception cref="DownstreamServiceException">Stock returned a 5xx, timed out, or was unreachable.</exception>
    Task<IReadOnlyList<StockReservationSnapshot>> ListByOrderAsync(Guid orderId, CancellationToken cancellationToken);
}
