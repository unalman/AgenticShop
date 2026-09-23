using AgenticShop.Catalog.Data;
using AgenticShop.Catalog.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AgenticShop.Catalog.Errors;

/// <summary>
/// Turns exceptions into RFC 9457 ProblemDetails so every AgenticShop service reports
/// failures in the same shape — that shared contract is why v1 needs no
/// AgenticShop.Shared project.
/// </summary>
/// <remarks>
/// Two rules govern the mapping. A client error must never surface as a 5xx: 5xx metrics
/// and error logs are how operators decide whether to page someone, so misclassifying a
/// malformed request turns ordinary bad input into false alarms that any caller can
/// generate at will. And no exception message is ever returned to the client — those
/// messages carry internal parameter names, provider detail and server-culture
/// formatting. They are logged, where the correlation id ties them back to the response.
/// </remarks>
public sealed class CatalogExceptionHandler(ILogger<CatalogExceptionHandler> logger) : IExceptionHandler
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

            // Must precede DbUpdateException, which it derives from.
            case DbUpdateConcurrencyException:
                return (StatusCodes.Status409Conflict, ConflictTitle,
                    "The product was changed by another request. Reload it and try again.");

            case DbUpdateException update when UniqueConstraintName(update) is { } constraint:
                return (StatusCodes.Status409Conflict, ConflictTitle, UniqueViolationDetail(constraint));

            // A value the database will not fit is the caller's error, not a server fault.
            // DataAnnotations normally catch these first; this arm is defence in depth for
            // the day a column is added without a matching limit on the contract, which is
            // exactly how an over-length SKU came to return 500 in the first place.
            case DbUpdateException update when ValueRangeDetail(update) is { } detail:
                return (StatusCodes.Status400BadRequest, BadRequestTitle, detail);

            // A domain invariant was violated. DataAnnotations catch the same conditions at
            // the boundary with field-level detail; this arm is the backstop for invariants
            // the contract does not express.
            case ArgumentException:
                return (StatusCodes.Status400BadRequest, BadRequestTitle, null);

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
    /// Keyed on the constraint name so the message stays true when a second unique index
    /// is added. A hardcoded "duplicate SKU" would otherwise be reported for any unique
    /// violation on the table.
    /// </summary>
    private static string UniqueViolationDetail(string? constraintName) => constraintName switch
    {
        ProductConfiguration.UniqueSkuIndexName => "A product with that SKU already exists.",
        _ => "The value conflicts with an existing record."
    };
}
