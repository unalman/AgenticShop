using Serilog;
using Serilog.Events;

namespace AgenticShop.Shared.Logging;

/// <summary>
/// The parts of a service's logging that configuration cannot express: ambient enrichment and the
/// access-log severity policy. Levels and category overrides live in each service's
/// <c>appsettings.json</c>, where the reasoning for each is recorded next to it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is service-specific, which is why it is shared rather than copied — the same call the
/// extraction of the validation filter and the correlation middleware made. It began life as
/// Catalog's <c>CatalogLogging</c>, written as the reference implementation before the other two
/// services adopted Serilog.
/// </para>
/// <para>
/// Deliberately split from the sink. <see cref="AddServiceLogging"/> returns a configured pipeline
/// with no sink attached, so a test can build the real configuration — levels included — and attach a
/// collector instead of writing to the console.
/// </para>
/// <para>
/// There is deliberately no <c>ILogEventFilter</c> here. One was written, to suppress EF Core's
/// Error-level command-failure entry only when a service's handler would answer it as a client error,
/// and it could never have worked: EF Core logs that entry from inside <c>RelationalCommand</c> and
/// rethrows without attaching the exception, so the event carries no exception to inspect — verified
/// by reading a real log line, after the filter's own tests had passed. Nothing available on that
/// event distinguishes a handled failure from an unhandled one, and nothing can, because the handler
/// has not run yet. The category is silenced by level instead; see each service's
/// <c>appsettings.json</c>.
/// </para>
/// </remarks>
public static class ServiceLogging
{
    /// <summary>
    /// The property every line written while handling a request carries. Named to match the
    /// <c>{CorrelationId}</c> placeholder the shared exception handler already puts in its message
    /// templates, so its entries and EF Core's agree on one property name rather than two.
    /// </summary>
    public const string CorrelationIdProperty = "CorrelationId";

    /// <summary>
    /// <see cref="CorrelationIdProperty"/> renders empty for lines written outside a request — at
    /// startup, for instance — which leaves a double space after the level. Accepted rather than
    /// worked around: Serilog templates have no conditional segments, and every alternative costs
    /// more than a space.
    /// </summary>
    public const string ConsoleOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static LoggerConfiguration AddServiceLogging(
        this LoggerConfiguration configuration,
        IConfiguration config)
        => configuration
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext();

    /// <summary>
    /// Access logging carries no severity judgement, so this ignores the status code and any
    /// exception and always returns <see cref="LogEventLevel.Information"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library default returns Error for a status above 499 or a non-null exception. That would
    /// put a second Error line next to the one the exception handler already writes, and the handler
    /// is the authoritative one: it is the component that classified the failure and chose the
    /// status. Keeping severity in a single place is the rule this repository already states as an
    /// invariant — the handler decides severity, not EF Core, and not the access log either.
    /// </para>
    /// <para>
    /// Note what this does <b>not</b> claim. The handler's split is by status, not by
    /// handled-versus-unhandled: anything it maps to a 5xx is logged at Error with the exception
    /// object, including Ordering's classified 502 for a dependency that failed. So a 502 still
    /// produces one Error line, correctly — this policy only stops the access log from adding a
    /// second one that says nothing the handler did not.
    /// </para>
    /// </remarks>
    public static LogEventLevel RequestLogLevel(HttpContext httpContext, double elapsedMs, Exception? exception)
        => LogEventLevel.Information;
}
