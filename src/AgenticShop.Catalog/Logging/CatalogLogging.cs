using Serilog;
using Serilog.Events;

namespace AgenticShop.Catalog.Logging;

/// <summary>
/// The parts of Catalog's logging that configuration cannot express: ambient enrichment and the
/// access-log severity policy. Levels and category overrides live in <c>appsettings.json</c>, where
/// the reasoning for each is recorded next to it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately split from the sink. <see cref="AddCatalogLogging"/> returns a configured pipeline
/// with no sink attached, so a test can build the real configuration — levels included — and attach
/// a collector instead of writing to the console.
/// </para>
/// <para>
/// There is deliberately no <c>ILogEventFilter</c> here. One was written, to suppress EF Core's
/// Error-level command-failure entry only when the handler would answer it as a client error, and it
/// could never have worked: EF Core logs that entry from inside <c>RelationalCommand</c> and rethrows
/// without attaching the exception, so the event carries no exception to inspect — verified by
/// reading a real log line, after the filter's own tests passed. Nothing available on that event
/// distinguishes a handled failure from an unhandled one, and nothing can, because the handler has
/// not run yet. The category is silenced by level instead; see <c>appsettings.json</c>.
/// </para>
/// </remarks>
public static class CatalogLogging
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

    public static LoggerConfiguration AddCatalogLogging(
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
    /// put a second Error line next to the one the exception handler already writes — and the
    /// handler's is the authoritative one, because only it knows whether the failure was handled.
    /// Keeping severity in a single place is the rule this repository already states as an
    /// invariant: the handler decides severity, not EF Core, and not the access log either.
    /// </para>
    /// <para>
    /// This matters more once the policy is copied to Ordering, whose handled 502 (a dependency that
    /// failed) would otherwise be logged at Error by the access log while the handler correctly
    /// treats it as a handled rejection.
    /// </para>
    /// </remarks>
    public static LogEventLevel RequestLogLevel(HttpContext httpContext, double elapsedMs, Exception? exception)
        => LogEventLevel.Information;
}
