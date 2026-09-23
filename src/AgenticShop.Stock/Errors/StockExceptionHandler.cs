using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using AgenticShop.Stock.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AgenticShop.Stock.Errors;

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
/// A deliberate copy of Catalog's handler rather than a shared type. Everything except
/// <see cref="UniqueViolationDetail"/>, the conflict wording and the two domain arms below is
/// identical; <c>docs/DECISIONS.md</c> records that a third copy — Ordering — is the trigger
/// to extract this into shared infrastructure.
/// </para>
/// </remarks>
public sealed class StockExceptionHandler(ILogger<StockExceptionHandler> logger) : IExceptionHandler
{
    private const string ConflictTitle = "Conflict.";
    private const string BadRequestTitle = "Bad request.";
    private const string UnexpectedTitle = "An unexpected error occurred.";

    /// <summary>
    /// Bounds the exception-chain walk so a pathological or self-referencing chain cannot
    /// produce an unbounded log line.
    /// </summary>
    private const int MaxChainDepth = 5;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContext.GetCorrelationId();

        // A client hanging up or timing out is ordinary traffic, not a fault. Logging it as
        // an error would make every aborted request look like an incident, and there is no
        // client left to receive a response, so hand it back to the framework.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(
                "Request cancelled by the client on {Method} {Path}. Correlation {CorrelationId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);

            return false;
        }

        var (statusCode, title, detail) = Classify(exception);

        if (statusCode >= StatusCodes.Status500InternalServerError)
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
                statusCode,
                httpContext.Request.Method,
                httpContext.Request.Path,
                DescribeForLog(exception),
                correlationId);
        }

        await Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            instance: httpContext.Request.Path,
            extensions: new Dictionary<string, object?> { ["correlationId"] = correlationId })
            .ExecuteAsync(httpContext);

        return true;
    }

    private static (int StatusCode, string Title, string? Detail) Classify(Exception exception)
    {
        switch (exception)
        {
            // Binding failures: malformed or absent JSON, a value that cannot be converted
            // to the target type, a non-numeric route or query parameter. The framework has
            // already decided these are the client's fault and records the status it wants,
            // so read it rather than assuming 400 — an oversized body is a 413.
            case BadHttpRequestException badRequest:
                return (badRequest.StatusCode, BadRequestTitle, null);

            // Not enough stock to honour the hold. A 409 rather than a 400: the request is
            // well-formed and conflicts with the resource's current state, and a retry after
            // restocking can succeed. The detail is built from the typed properties, never
            // from Message, so the vocabulary stays ours.
            case InsufficientStockException insufficient:
                return (StatusCodes.Status409Conflict, ConflictTitle,
                    $"Only {insufficient.Available} unit(s) are available; {insufficient.Requested} were requested.");

            // The reservation has already settled. Ordering must treat this as success when it
            // is retrying a confirm it already issued — see docs/ROADMAP.md.
            case InvalidReservationStateException invalidState:
                return (StatusCodes.Status409Conflict, ConflictTitle,
                    $"The reservation is already {invalidState.CurrentStatus.ToString().ToLowerInvariant()} " +
                    $"and cannot be {invalidState.AttemptedStatus.ToString().ToLowerInvariant()}.");

            // Must precede DbUpdateException, which it derives from. On Stock this is the
            // overselling guard: two concurrent reservations that both read the same stale
            // Available count, one of which must lose.
            case DbUpdateConcurrencyException:
                return (StatusCodes.Status409Conflict, ConflictTitle,
                    "The stock record was changed by another request. Reload it and try again.");

            case DbUpdateException update when UniqueConstraintName(update) is { } constraint:
                return (StatusCodes.Status409Conflict, ConflictTitle, UniqueViolationDetail(constraint));

            // A value the database will not fit is the caller's error, not a server fault.
            // DataAnnotations normally catch these first; this arm is defence in depth for
            // the day a column is added without a matching limit on the contract.
            case DbUpdateException update when ValueRangeDetail(update) is { } detail:
                return (StatusCodes.Status400BadRequest, BadRequestTitle, detail);

            // A domain invariant was violated. DataAnnotations catch the same conditions at
            // the boundary with field-level detail; this arm is the backstop for invariants
            // the contract does not express, such as an empty ProductId.
            case ArgumentException:
                return (StatusCodes.Status400BadRequest, BadRequestTitle, null);

            // Deliberately no arm for a CHECK-constraint violation (SQLSTATE 23514). Every
            // constraint on these tables restates an invariant StockItem already guards, and
            // xmin closes the concurrent path, so reaching one means this assembly has a bug.
            // That is a server fault and belongs in the 5xx bucket at Error level — mapping it
            // to 409 would hide our own defect inside the caller's error budget.
            // StockExceptionHandlerTests pins this behaviour.
            default:
                return (StatusCodes.Status500InternalServerError, UnexpectedTitle, null);
        }
    }

    /// <summary>
    /// Describes the whole exception chain, outermost first. Both ends matter: for a binding
    /// failure the outer message names the parameter that failed, while for a database
    /// failure the inner one carries the SQLSTATE that explains it. Logging only the
    /// outermost exception — EF's "see the inner exception for details" — reports nothing.
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
    /// Surfaces the fields an operator triages on. <see cref="PostgresException.MessageText"/>
    /// is used rather than <c>Message</c>, which prefixes the same SQLSTATE that is already
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
        StockItemConfiguration.UniqueProductIndexName =>
            "A stock record for that product already exists.",

        // Also the natural idempotency guard: a retried reserve for the same order and product
        // conflicts instead of holding the quantity twice.
        StockReservationConfiguration.UniqueOrderStockItemIndexName =>
            "That order already has a reservation for this product.",

        _ => "The value conflicts with an existing record."
    };
}
