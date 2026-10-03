using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Shared.Errors;

namespace AgenticShop.Ordering.Errors;

/// <summary>
/// Ordering's half of the error contract, and the only one that adds a status the other services
/// cannot need: **502**, for a dependency that failed.
/// </summary>
/// <remarks>
/// <para>
/// The four invariants, the classification order, the logging severity rules and the PostgreSQL
/// chain description all live in <see cref="ProblemDetailsExceptionHandler"/>. Behaviour is
/// specified once there; <c>OrderingExceptionHandlerTests</c> covers only what is Ordering-specific.
/// </para>
/// <para>
/// Two divergences from the other services, both forced by orchestration, and both expressed
/// through the shared surface rather than by forking it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A 502 bucket. Catalog and Stock have no dependencies, so their 5xx means only "this assembly has
/// a bug". Here it also means "we asked and were let down", which operators triage differently, so
/// it gets its own status rather than sharing 500. An <i>unexpected</i> 4xx from a downstream
/// service is still a 500 — it means we sent something its contract does not allow.
/// </description></item>
/// <item><description>
/// An <c>orderId</c> extension, supplied through
/// <see cref="ExceptionClassification.WithExtension"/>. A failed placement still writes a row, so a
/// 409 or 502 that names no order leaves the client unable to tell "nothing was created" from
/// "something was created and is broken" — and that ambiguity is what causes blind retries against
/// a non-idempotent endpoint.
/// </description></item>
/// </list>
/// </remarks>
public sealed class OrderingExceptionHandler(ILogger<OrderingExceptionHandler> logger)
    : ProblemDetailsExceptionHandler(logger)
{
    private const string DownstreamTitle = "Downstream failure.";

    /// <summary>
    /// Exposed for <c>OrderEndpoints</c>' idempotency replay path, which rebuilds a recorded failure
    /// without going through this handler and must therefore produce the same titles rather than
    /// inventing parallel ones.
    /// </summary>
    internal const string ConflictFailureTitle = ConflictTitle;

    internal const string DownstreamFailureTitle = DownstreamTitle;

    private const string DownstreamDetail =
        "The request could not be completed because a downstream service did not respond.";

    private const string PartiallyConfirmedDetail =
        "The order was partially confirmed and requires reconciliation.";

    protected override string ConcurrencyDetail =>
        "The order was changed by another request. Reload it and try again.";

    protected override ExceptionClassification? ClassifyDomain(Exception exception) => exception switch
    {
        // The product cannot be ordered at all. A 409 rather than a 400: the request is well-formed
        // and conflicts with the catalog's current state, and a retry after the product is
        // reinstated can succeed. Same reasoning Stock applies to insufficient stock.
        ProductUnavailableException unavailable => new(
            StatusCodes.Status409Conflict,
            ConflictTitle,
            $"Product {unavailable.ProductId} cannot be ordered."),

        // The lines do not share a currency, so their prices cannot be added. A 400, and like every
        // other domain 400 in the repository its detail is null: invariant three says no exception
        // message reaches the client. The offending codes are on the exception's typed property and
        // in the log line, where the correlation id ties them to this response.
        MixedCurrencyException => new(StatusCodes.Status400BadRequest, BadRequestTitle, null),

        // Stock refused a line, so every hold taken was released and the order was written as
        // Failed. The order id goes back with it: the row exists and is queryable, and without the
        // id the client cannot reach it.
        StockUnavailableException noStock => ExceptionClassification.WithExtension(
            StatusCodes.Status409Conflict,
            ConflictTitle,
            $"Not enough stock is available for product {noStock.ProductId}.",
            "orderId",
            noStock.OrderId),

        // Placement reached a terminal state that is not Confirmed because a dependency failed. The
        // row is already written, so the id is returned with the failure.
        OrderPlacementIncompleteException incomplete => ExceptionClassification.WithExtension(
            StatusCodes.Status502BadGateway,
            DownstreamTitle,
            incomplete.Status == OrderStatus.PartiallyConfirmed
                ? PartiallyConfirmedDetail
                : DownstreamDetail,
            "orderId",
            incomplete.OrderId),

        // Another request holds this key and has not recorded an outcome yet, so there is nothing to
        // replay. A 409 rather than a 425 or a 503: the request is well-formed and conflicts with
        // the key's current state, and retrying the same key after the first attempt settles is
        // exactly what should succeed. The key itself stays out of the body — it is untrusted input
        // and echoing it back tells the caller nothing it did not just send — but it is on the
        // exception, so the log line names it.
        IdempotencyKeyInUseException => new(
            StatusCodes.Status409Conflict,
            ConflictTitle,
            "That idempotency key is already in use and has no recorded outcome yet. " +
            "Wait for the first attempt to settle, then retry the same key."),

        // No order exists for this one: the failure came from Catalog, before anything was held. The
        // service name stays in the log and out of the body, because naming our dependencies in a
        // response discloses internal topology.
        DownstreamServiceException => new(
            StatusCodes.Status502BadGateway,
            DownstreamTitle,
            DownstreamDetail),

        _ => null
    };

    /// <summary>
    /// Keyed on the constraint name so the message stays true as indexes are added.
    /// </summary>
    protected override string UniqueViolationDetail(string? constraintName) => constraintName switch
    {
        OrderConfiguration.UniqueOrderNumberIndexName =>
            "That order number already exists.",

        // Also the schema-level form of the "one line per product" rule, which the placement path
        // enforces by combining duplicate lines before it can be reached.
        OrderLineConfiguration.UniqueOrderProductIndexName =>
            "That order already has a line for this product.",

        // Unreachable in practice — OrderPlacer.ClaimAsync catches this violation and raises
        // IdempotencyKeyInUseException, which has a better message — but the constraint name is
        // declared for the handler to branch on, so it is branched on. Without this case a lost race
        // that escaped the catch would report a generic conflict.
        OrderIdempotencyKeyConfiguration.PrimaryKeyName =>
            "That idempotency key is already in use.",

        _ => FallbackConflictDetail
    };
}
