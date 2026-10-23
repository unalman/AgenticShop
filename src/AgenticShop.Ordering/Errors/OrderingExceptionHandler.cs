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
/// <b>One divergence from the other services</b>, forced by orchestration and expressed through the
/// shared surface rather than by forking it: a 502 bucket. Catalog and Stock have no dependencies, so
/// their 5xx means only "this assembly has a bug". Here it also means "we asked and were let down",
/// which operators triage differently, so it gets its own status rather than sharing 500. An
/// <i>unexpected</i> 4xx from a downstream service is still a 500 — it means we sent something its
/// contract does not allow.
/// </para>
/// <para>
/// There was a second, and it is worth knowing why it went quiet rather than assuming it is still
/// load-bearing. An <c>orderId</c> extension, attached through
/// <see cref="ExceptionClassification.WithExtension"/>, existed because a failed placement used to
/// write a row — and a 409 or 502 naming no order left the client unable to tell "nothing was created"
/// from "something was created and is broken", which is the ambiguity that causes blind retries against
/// a non-idempotent endpoint. <b>Nothing emits it now.</b> A placement fails while resolving against
/// Catalog, before any row exists, and an order is settled later by <c>StockOutcomeConsumer</c>, which
/// has no response to attach an extension to. The mechanism stays in Shared deliberately: it is a
/// general capability of the classification, and the hazard it was built for returns with any path that
/// writes a row and then fails inside the request.
/// </para>
/// </remarks>
public sealed class OrderingExceptionHandler(ILogger<OrderingExceptionHandler> logger)
    : ProblemDetailsExceptionHandler(logger)
{
    private const string DownstreamTitle = "Downstream failure.";

    private const string DownstreamDetail =
        "The request could not be completed because a downstream service did not respond.";

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
