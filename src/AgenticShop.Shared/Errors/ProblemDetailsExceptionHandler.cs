using AgenticShop.Shared.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AgenticShop.Shared.Errors;

/// <summary>
/// The shared half of every AgenticShop exception handler: the cancellation arm, the severity
/// decision, the logging, the ProblemDetails shape, and the classification arms that mean the same
/// thing in every service. A service derives from this and supplies only its own domain arms.
/// </summary>
/// <remarks>
/// <para>
/// Four invariants govern the mapping, and all four are test-guarded. <b>A client error is never a
/// 5xx</b> — 5xx rates and error logs are how operators decide whether to page someone, so
/// misclassifying bad input creates false alarms any caller can generate at will. <b>A server fault
/// is never a 4xx</b> — a violated <c>CHECK</c> restates an invariant the entity already guards, so
/// it can only mean our code has a bug, and reporting it as 409 would hide that inside the caller's
/// error budget. <b>No exception message reaches the client</b> — those messages carry internal
/// parameter names and are formatted with the server's culture. <b>The handler decides severity, not
/// EF Core</b> — handled rejections log one Warning line with no exception object, unhandled
/// failures log Error with it, which is why <c>Microsoft.EntityFrameworkCore.Update</c> is silenced
/// in <c>appsettings.json</c>.
/// </para>
/// <para>
/// There is deliberately no arm for a <c>CHECK</c> violation (<c>23514</c>) or for
/// <see cref="InvalidOperationException"/>. Both fall through to the 500 by design; see the second
/// invariant above.
/// </para>
/// </remarks>
public abstract class ProblemDetailsExceptionHandler(ILogger logger) : IExceptionHandler
{
    protected const string ConflictTitle = "Conflict.";
    protected const string BadRequestTitle = "Bad request.";
    protected const string UnexpectedTitle = "An unexpected error occurred.";

    /// <summary>
    /// What a unique violation reports when the constraint has no dedicated message. Services
    /// return this from the fallback arm of <see cref="UniqueViolationDetail"/> so a new index
    /// cannot be misreported as whichever conflict happens to be listed first.
    /// </summary>
    protected const string FallbackConflictDetail = "The value conflicts with an existing record.";

    protected const string TooLongDetail = "A submitted value is too long for its field.";
    protected const string OutOfRangeDetail = "A submitted value is out of range for its field.";

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
        // left to receive a response, so hand it back to the framework. The arm is guarded on
        // RequestAborted so a genuine internal timeout still counts as a fault.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(
                "Request cancelled by the client on {Method} {Path}. Correlation {CorrelationId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);

            return false;
        }

        // Domain arms first, then the shared ones. The order is load-bearing: a service's own
        // exceptions must be recognised before the DbUpdateException arms can claim them, and
        // DbUpdateConcurrencyException must precede DbUpdateException because it derives from it.
        var classification = ClassifyDomain(exception) ?? ClassifyShared(exception);

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
            // Message only, no exception object: a handled rejection is not an incident and does
            // not warrant a stack trace. The description walks to the root cause, so a
            // DbUpdateException reports the PostgreSQL error that actually explains it rather than
            // EF's "see the inner exception for details" wrapper.
            logger.LogWarning(
                "Request rejected with {StatusCode} on {Method} {Path}: {Reason} | Correlation {CorrelationId}",
                classification.StatusCode,
                httpContext.Request.Method,
                httpContext.Request.Path,
                DescribeForLog(exception),
                correlationId);
        }

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["correlationId"] = correlationId
        };

        if (classification.Extensions is { } extra)
        {
            foreach (var (name, value) in extra)
            {
                extensions[name] = value;
            }
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

    /// <summary>
    /// Classifies the exceptions this service owns. Return <c>null</c> to fall through to the
    /// shared arms — every arm the service does not recognise must return null, not a 500, or the
    /// shared classification never runs. The default is null, so a service with no domain
    /// exceptions of its own does not override this at all.
    /// </summary>
    protected virtual ExceptionClassification? ClassifyDomain(Exception exception) => null;

    /// <summary>
    /// The message for a unique violation, keyed on the constraint name so it stays true as
    /// indexes are added. Return <see cref="FallbackConflictDetail"/> for an unrecognised name.
    /// </summary>
    protected abstract string UniqueViolationDetail(string? constraintName);

    /// <summary>
    /// The 409 detail for an optimistic-concurrency conflict, naming the resource the caller must
    /// reload.
    /// </summary>
    protected abstract string ConcurrencyDetail { get; }

    private ExceptionClassification ClassifyShared(Exception exception)
    {
        switch (exception)
        {
            // Binding failures: malformed or absent JSON, a value that cannot be converted to the
            // target type, a non-numeric route or query parameter. The framework has already
            // decided these are the client's fault and records the status it wants, so read it
            // rather than assuming 400 — an oversized body is a 413.
            case BadHttpRequestException badRequest:
                return new(badRequest.StatusCode, BadRequestTitle, null);

            // Must precede DbUpdateException, which it derives from.
            case DbUpdateConcurrencyException:
                return new(StatusCodes.Status409Conflict, ConflictTitle, ConcurrencyDetail);

            case DbUpdateException update when UniqueConstraintName(update) is { } constraint:
                return new(StatusCodes.Status409Conflict, ConflictTitle, UniqueViolationDetail(constraint));

            // A value the database will not fit is the caller's error, not a server fault.
            // DataAnnotations normally catch these first; this arm is defence in depth for the day
            // a column is added without a matching limit on the contract.
            case DbUpdateException update when ValueRangeDetail(update) is { } detail:
                return new(StatusCodes.Status400BadRequest, BadRequestTitle, detail);

            // A domain invariant was violated. DataAnnotations catch the same conditions at the
            // boundary with field-level detail; this arm is the backstop for invariants the
            // contract does not express.
            case ArgumentException:
                return new(StatusCodes.Status400BadRequest, BadRequestTitle, null);

            // Deliberately no arm for a CHECK violation (23514) or for InvalidOperationException.
            // Both mean this assembly has a bug, and a server fault is never a 4xx.
            default:
                return new(StatusCodes.Status500InternalServerError, UnexpectedTitle, null);
        }
    }

    /// <summary>
    /// Describes the whole exception chain, outermost first. Both ends matter: for a binding
    /// failure the outer message names the parameter that failed, while for a database failure the
    /// inner one carries the SQLSTATE that explains it. Logging only the outermost exception — EF's
    /// "see the inner exception for details" — reports nothing.
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
    /// used rather than <c>Message</c>, which prefixes the same SQLSTATE that is already reported
    /// separately.
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
    /// PostgreSQL rejects a value that does not fit its column with a data error rather than a
    /// connection error. Those are caused by the request, so they are 400s.
    /// </summary>
    private static string? ValueRangeDetail(DbUpdateException exception) => SqlStateOf(exception) switch
    {
        PostgresErrorCodes.StringDataRightTruncation => TooLongDetail,
        PostgresErrorCodes.NumericValueOutOfRange => OutOfRangeDetail,
        _ => null
    };
}
