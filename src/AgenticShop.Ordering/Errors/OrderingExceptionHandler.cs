using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AgenticShop.Ordering.Errors;

/// <summary>
/// Turns exceptions into RFC 9457 ProblemDetails so every AgenticShop service reports
/// failures in the same shape — that shared contract is why v1 needs no
/// AgenticShop.Shared project.
/// </summary>
/// <remarks>
/// <para>
/// Two rules govern the mapping, and they run in both directions. A client error must never
/// surface as a 5xx: 5xx metrics and error logs are how operators decide whether to page
/// someone, so misclassifying a malformed request turns ordinary bad input into false alarms
/// that any caller can generate at will. Conversely a server fault must never be reported as
/// a 4xx, or a genuine bug is hidden inside the caller's error budget. And no exception
/// message is ever returned to the client — those messages carry internal parameter names,
/// provider detail and server-culture formatting. They are logged, where the correlation id
/// ties them back to the response.
/// </para>
/// <para>
/// The third and last deliberate copy of Catalog's handler rather than a shared type; extraction
/// is deferred to Phase 1 by decision — see <c>docs/DECISIONS.md</c>. Everything except the
/// domain arms below, <see cref="UniqueViolationDetail"/> and the <c>orderId</c> extension is
/// identical to Stock's.
/// </para>
/// <para>
/// Two divergences from the copied skeleton, both forced by orchestration:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Classify</c> also yields an optional order id. A failed placement still writes a row, so a
/// 409 or 502 that names no order leaves the client unable to tell "nothing was created" from
/// "something was created and is broken" — and that ambiguity is what causes blind retries against
/// a non-idempotent endpoint.
/// </description></item>
/// <item><description>
/// A 502 arm exists at all. Catalog and Stock have no dependencies, so their 5xx bucket means only
/// "this assembly has a bug". Here it also means "we asked and were let down", which operators
/// triage differently, so it gets its own status rather than sharing 500.
/// </description></item>
/// </list>
/// </remarks>
public sealed class OrderingExceptionHandler(ILogger<OrderingExceptionHandler> logger) : IExceptionHandler
{
    private const string ConflictTitle = "Conflict.";
    private const string BadRequestTitle = "Bad request.";
    private const string UnexpectedTitle = "An unexpected error occurred.";
    private const string DownstreamTitle = "Downstream failure.";

    private const string DownstreamDetail =
        "The request could not be completed because a downstream service did not respond.";

    private const string PartiallyConfirmedDetail =
        "The order was partially confirmed and requires reconciliation.";

    /// <summary>
    /// Bounds the exception-chain walk so a pathological or self-referencing chain cannot produce
    /// an unbounded log line.
    /// </summary>
    private const int MaxChainDepth = 5;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContext.GetCorrelationId();

        // A client hanging up or timing out is ordinary traffic, not a fault. Logging it as an
        // error would make every aborted request look like an incident, and there is no client
        // left to receive a response, so hand it back to the framework.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(
                "Request cancelled by the client on {Method} {Path}. Correlation {CorrelationId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);

            return false;
        }

        var classification = Classify(exception);

        if (classification.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                exception,
                "Unhandled exception on {Method} {Path}. Correlation {CorrelationId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);
        }
        else
        {
            // Message only, no exception object: a handled rejection is not an incident and
            // does not warrant a stack trace. The description walks to the root cause, so a
            // DbUpdateException reports the PostgreSQL error that actually explains it rather
            // than EF's "see the inner exception for details" wrapper.
            logger.LogWarning(
                "Request rejected with {StatusCode} on {Method} {Path}: {Reason} | Correlation {CorrelationId}",
                classification.StatusCode,
                httpContext.Request.Method,
                httpContext.Request.Path,
                DescribeForLog(exception),
                correlationId);
        }

        var extensions = new Dictionary<string, object?> { ["correlationId"] = correlationId };

        if (classification.OrderId is { } orderId)
        {
            extensions["orderId"] = orderId;
        }

        await Results.Problem(
            title: classification.Title,
            detail: classification.Detail,
            statusCode: classification.StatusCode,
            instance: httpContext.Request.Path,
            extensions: extensions)
            .ExecuteAsync(httpContext);

        return true;
    }

    private static Classification Classify(Exception exception)
    {
        switch (exception)
        {
            // Binding failures: malformed or absent JSON, a value that cannot be converted to the
            // target type, a non-numeric route or query parameter. The framework has already
            // decided this is the client's fault and records the status it wants, so read it rather
            // than assuming 400 — an oversized body is a 413.
            case BadHttpRequestException badRequest:
                return new(badRequest.StatusCode, BadRequestTitle, null, null);

            // The product cannot be ordered at all. A 409 rather than a 400: the request is
            // well-formed and conflicts with the catalog's current state, and a retry after the
            // product is reinstated can succeed. That is the same reasoning Stock applies to
            // insufficient stock.
            case ProductUnavailableException unavailable:
                return new(StatusCodes.Status409Conflict, ConflictTitle,
                    $"Product {unavailable.ProductId} cannot be ordered.", null);

            // The lines do not share a currency, so their prices cannot be added. A 400, and like
            // every other domain 400 in the repository its detail is null: invariant three says no
            // exception message reaches the client. The offending codes are on the exception's typed
            // property and in the log line, where the correlation id ties them to this response.
            case MixedCurrencyException:
                return new(StatusCodes.Status400BadRequest, BadRequestTitle, null, null);

            // Stock refused a line, so every hold taken was released and the order was written as
            // Failed. The order id goes back with it: the row exists and is queryable, and without
            // the id the client cannot reach it.
            case StockUnavailableException noStock:
                return new(StatusCodes.Status409Conflict, ConflictTitle,
                    $"Not enough stock is available for product {noStock.ProductId}.",
                    noStock.OrderId);

            // Placement reached a terminal state that is not Confirmed because a dependency failed.
            // The row is already written, so the id is returned with the failure.
            case OrderPlacementIncompleteException incomplete:
                return new(StatusCodes.Status502BadGateway, DownstreamTitle,
                    incomplete.Status == OrderStatus.PartiallyConfirmed
                        ? PartiallyConfirmedDetail
                        : DownstreamDetail,
                    incomplete.OrderId);

            // No order exists for this one: the failure came from Catalog, before anything was
            // held. The service name stays in the log and out of the body, because naming our
            // dependencies in a response discloses internal topology.
            case DownstreamServiceException:
                return new(StatusCodes.Status502BadGateway, DownstreamTitle, DownstreamDetail, null);

            // Must precede DbUpdateException, which it derives from. There is no update path in
            // Phase 0 — an order is written once — so this arm is currently unreachable; it is here
            // because the token is mandatory on every entity and Phase 2's saga will exercise it.
            case DbUpdateConcurrencyException:
                return new(StatusCodes.Status409Conflict, ConflictTitle,
                    "The order was changed by another request. Reload it and try again.", null);

            case DbUpdateException update when UniqueConstraintName(update) is { } constraint:
                return new(StatusCodes.Status409Conflict, ConflictTitle, UniqueViolationDetail(constraint), null);

            // A value the database will not fit is the caller's error, not a server fault.
            // DataAnnotations normally catch these first; this arm is defence in depth for the day
            // a column is added without a matching limit on the contract.
            case DbUpdateException update when ValueRangeDetail(update) is { } detail:
                return new(StatusCodes.Status400BadRequest, BadRequestTitle, detail, null);

            // A domain invariant was violated. DataAnnotations catch the same conditions at the
            // boundary with field-level detail; this arm is the backstop for invariants the
            // contract does not express, such as an empty ProductId.
            case ArgumentException:
                return new(StatusCodes.Status400BadRequest, BadRequestTitle, null, null);

            // Deliberately no arm for a CHECK-constraint violation (SQLSTATE 23514), and none for
            // InvalidOperationException either — which is what Order throws on a double transition.
            // Every CHECK restates an invariant the entity already guards and no caller input can
            // reach either, so both mean this assembly has a bug. That is a server fault and
            // belongs in the 5xx bucket at Error level; mapping it to 409 would hide our own defect
            // inside the caller's error budget. OrderingExceptionHandlerTests pins both.
            default:
                return new(StatusCodes.Status500InternalServerError, UnexpectedTitle, null, null);
        }
    }

    /// <summary>
    /// Describes the whole exception chain, outermost first. Both ends matter: for a binding
    /// failure the outer message names the parameter that failed, while for a database failure
    /// the inner one carries the SQLSTATE that explains it. Logging only the outermost exception
    /// — EF's "see the inner exception for details" — reports nothing.
    /// </summary>
    private static string DescribeForLog(Exception exception)
    {
        var chain = new List<string>(MaxChainDepth);
        Exception? current = exception;

        while (current is not null && chain.Count < MaxChainDepth)
        {
            chain.Add(Describe(current));

            current = ReferenceEquals(current.InnerException, current) ? null : current.InnerException;
        }

        return string.Join(" -> ", chain);
    }

    private static string Describe(Exception exception) => exception switch
    {
        PostgresException postgres => DescribePostgres(postgres),
        _ => $"{exception.GetType().Name}: {exception.Message}"
    };

    /// <summary>
    /// Surfaces the fields an operator triages on. <see cref="PostgresException.MessageText"/> is
    /// used rather than <c>Message</c>, which prefixes the same SQLSTATE that is already
    /// reported separately.
    /// </summary>
    private static string DescribePostgres(PostgresException postgres)
    {
        var fields = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(postgres.TableName))
        {
            fields.Add($"table={postgres.TableName}");
        }

        if (!string.IsNullOrWhiteSpace(postgres.ColumnName))
        {
            fields.Add($"column={postgres.ColumnName}");
        }

        if (!string.IsNullOrWhiteSpace(postgres.ConstraintName))
        {
            fields.Add($"constraint={postgres.ConstraintName}");
        }

        var suffix = fields.Count > 0 ? $" ({string.Join(", ", fields)})" : string.Empty;

        return $"PostgresException {postgres.SqlState}: {postgres.MessageText}{suffix}";
    }

    private static string? SqlStateOf(DbUpdateException exception)
        => (exception.InnerException as PostgresException)?.SqlState;

    private static string? UniqueConstraintName(DbUpdateException exception)
        => SqlStateOf(exception) == PostgresErrorCodes.UniqueViolation
            ? ((PostgresException)exception.InnerException!).ConstraintName
            : null;

    /// <summary>
    /// PostgreSQL rejects a value that does not fit its column with a data error rather
    /// than a connection error. Those are caused by the request, so they are 400s.
    /// </summary>
    private static string? ValueRangeDetail(DbUpdateException exception) => SqlStateOf(exception) switch
    {
        PostgresErrorCodes.StringDataRightTruncation => "A submitted value is too long for its field.",
        PostgresErrorCodes.NumericValueOutOfRange => "A submitted value is out of range for its field.",
        _ => null
    };

    /// <summary>
    /// Keyed on the constraint name so the message stays true as indexes are added. A
    /// hardcoded message would otherwise be reported for any unique violation on the table.
    /// </summary>
    private static string UniqueViolationDetail(string? constraintName) => constraintName switch
    {
        OrderConfiguration.UniqueOrderNumberIndexName =>
            "That order number already exists.",

        // Also the schema-level form of the "one line per product" rule, which the placement path
        // enforces by combining duplicate lines before it can be reached.
        OrderLineConfiguration.UniqueOrderProductIndexName =>
            "That order already has a line for this product.",

        _ => "The value conflicts with an existing record."
    };

    private sealed record Classification(int StatusCode, string Title, string? Detail, Guid? OrderId);
}
