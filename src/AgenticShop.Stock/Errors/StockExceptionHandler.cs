using AgenticShop.Shared.Errors;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;

namespace AgenticShop.Stock.Errors;

/// <summary>
/// Stock's half of the error contract: the two reservation conflicts, and the messages that name a
/// Stock resource. Everything else comes from the shared skeleton.
/// </summary>
/// <remarks>
/// The four invariants, the classification order, the logging severity rules and the PostgreSQL
/// chain description all live in <see cref="ProblemDetailsExceptionHandler"/>. Behaviour is
/// specified once there; <c>StockExceptionHandlerTests</c> covers only what is Stock-specific.
/// </remarks>
public sealed class StockExceptionHandler(ILogger<StockExceptionHandler> logger)
    : ProblemDetailsExceptionHandler(logger)
{
    protected override string ConcurrencyDetail =>
        "The stock record was changed by another request. Reload it and try again.";

    protected override ExceptionClassification? ClassifyDomain(Exception exception) => exception switch
    {
        // Not enough stock to honour the hold. A 409 rather than a 400: the request is well-formed
        // and conflicts with the resource's current state, and a retry after restocking can
        // succeed. The detail is built from the typed properties, never from Message, so the
        // vocabulary stays ours.
        InsufficientStockException insufficient => new(
            StatusCodes.Status409Conflict,
            ConflictTitle,
            $"Only {insufficient.Available} unit(s) are available; {insufficient.Requested} were requested."),

        // The reservation has already settled. Ordering must treat this as success when it is
        // retrying a confirm it already issued — see Ordering's decision O9.
        InvalidReservationStateException invalidState => new(
            StatusCodes.Status409Conflict,
            ConflictTitle,
            $"The reservation is already {invalidState.CurrentStatus.ToString().ToLowerInvariant()} " +
            $"and cannot be {invalidState.AttemptedStatus.ToString().ToLowerInvariant()}."),

        _ => null
    };

    /// <summary>
    /// Keyed on the constraint name so the message stays true as indexes are added.
    /// </summary>
    protected override string UniqueViolationDetail(string? constraintName) => constraintName switch
    {
        StockItemConfiguration.UniqueProductIndexName =>
            "A stock record for that product already exists.",

        // Also the natural idempotency guard: a retried reserve for the same order and product
        // conflicts instead of holding the quantity twice.
        StockReservationConfiguration.UniqueOrderStockItemIndexName =>
            "That order already has a reservation for this product.",

        _ => FallbackConflictDetail
    };
}
