using AgenticShop.Catalog.Errors;
using AgenticShop.Catalog.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace AgenticShop.Catalog.UnitTests;

/// <summary>
/// The logging decisions this service owns: what the shipped configuration admits, that the ambient
/// correlation id reaches every category, and that access logging makes no severity claim.
/// </summary>
/// <remarks>
/// <para>
/// Built from the shipped <c>appsettings.json</c> — copied to this project's output — with a
/// collector in place of the console sink. Nothing here asserts how Serilog works, only what our
/// configuration decides; and reading the real file is what makes a wrong level fail here instead of
/// at startup.
/// </para>
/// <para>
/// There is deliberately no test of an EF command-failure filter. One existed and was deleted: EF
/// Core attaches no exception to that event, so the filter could not have worked. The reasoning is in
/// <c>appsettings.json</c> and in <c>CatalogLogging</c>.
/// </para>
/// </remarks>
public class CatalogLoggingTests
{
    private const string EfUpdate = "Microsoft.EntityFrameworkCore.Update";
    private const string EfCommand = "Microsoft.EntityFrameworkCore.Database.Command";
    private const string EfMigrations = "Microsoft.EntityFrameworkCore.Migrations";

    private static readonly string HandlerCategory = typeof(CatalogExceptionHandler).FullName!;

    // --- What the shipped configuration admits ------------------------------------------

    [Fact]
    public void EfUpdateFailuresAreSilenced()
    {
        // The decision Serilog replaces rather than revisits: EF logs every SaveChanges failure at
        // Error and cannot know whether it was handled, so the handler owns severity instead.
        var sink = Run(EfUpdate, logger => logger.Error(FailedSaveChanges));

        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public void EfCommandFailuresAreSilenced()
    {
        // The behaviour that was actually broken. A routine duplicate SKU answered 409 used to emit
        // an Error entry here, and no filter can prevent it: EF writes this event before the handler
        // runs and attaches no exception, so nothing on it says whether the failure will be handled.
        var sink = Run(EfCommand, logger => logger.Error(FailedCommand));

        sink.Events.Should().BeEmpty(
            "a client error must not appear in the error log an operator pages from");
    }

    [Fact]
    public void SuccessfulCommandsStayOutOfTheLog()
    {
        var sink = Run(EfCommand, logger => logger.Information(ExecutedCommand));

        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public void TheHandlerKeepsItsSeveritySplit()
    {
        // The invariant that must survive the migration: a handled rejection is a Warning, an
        // unhandled failure an Error, and the handler remains the only thing that decides which.
        // Silencing EF's categories must not silence the handler's own.
        var sink = Run(HandlerCategory, logger =>
        {
            logger.Warning("Request rejected with {StatusCode} on {Method} {Path}: {Reason}.", 409, "POST", "/x", "duplicate");
            logger.Error(new InvalidOperationException("bug"), "Unhandled exception on {Method} {Path}.", "POST", "/x");
        });

        sink.Events.Should().SatisfyRespectively(
            first =>
            {
                first.Level.Should().Be(LogEventLevel.Warning);
                first.Exception.Should().BeNull("a handled rejection is not an incident");
            },
            second =>
            {
                second.Level.Should().Be(LogEventLevel.Error);
                second.Exception.Should().NotBeNull("the chain and stack trace must survive");
            });
    }

    [Fact]
    public void OrdinaryCategoriesAreUnaffected()
    {
        // Guarding against the two overrides above being read as "EF Core is silenced". Migrations
        // still logs, so the overrides are per category and not a blanket.
        var sink = Run(EfMigrations, logger => logger.Information("Applying migrations."));

        sink.Events.Should().ContainSingle()
            .Which.Level.Should().Be(LogEventLevel.Information);
    }

    // --- The ambient correlation id ------------------------------------------------------

    [Fact]
    public void TheAmbientCorrelationIdReachesEveryCategory()
    {
        // The mechanism Program.cs relies on: EF Core's lines carry no correlation id of their own,
        // so the id is pushed into the ambient context and Enrich.FromLogContext is what makes it
        // appear. Without the enrichment the push would be silently inert.
        var sink = new CollectingSink();
        var logger = ConfiguredLogger(sink);

        using (LogContext.PushProperty(CatalogLogging.CorrelationIdProperty, "smoke-correlation"))
        {
            logger.ForContext(Constants.SourceContextPropertyName, HandlerCategory)
                .Warning("Request rejected with {StatusCode}.", 409);

            logger.ForContext(Constants.SourceContextPropertyName, EfMigrations)
                .Information("Applying migrations.");
        }

        sink.Events.Should().HaveCount(2);

        foreach (var logEvent in sink.Events)
        {
            logEvent.Properties.Should()
                .ContainKey(CatalogLogging.CorrelationIdProperty, "every line for the request carries it");

            logEvent.Properties[CatalogLogging.CorrelationIdProperty]
                .ToString()
                .Should()
                .Contain("smoke-correlation");
        }
    }

    [Fact]
    public void TheCorrelationPropertyMatchesTheHandlersPlaceholder()
    {
        // The shared handler hardcodes {CorrelationId} in its message templates. If this constant
        // drifts from it, handler lines and EF lines would carry the same value under two names.
        CatalogLogging.CorrelationIdProperty.Should().Be("CorrelationId");

        CatalogLogging.ConsoleOutputTemplate
            .Should()
            .Contain("{" + CatalogLogging.CorrelationIdProperty, "or the template would render it empty");
    }

    // --- Access-log severity -------------------------------------------------------------

    [Fact]
    public void AccessLoggingMakesNoSeverityClaim()
    {
        // The library default returns Error for a status above 499 or a non-null exception, which
        // would put a second Error beside the one the handler already writes. Severity belongs to
        // the handler alone.
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var level = CatalogLogging.RequestLogLevel(
            context,
            elapsedMs: 12.5,
            new InvalidOperationException("bug"));

        level.Should().Be(LogEventLevel.Information);
    }

    // --- Helpers ------------------------------------------------------------------------

    private const string FailedCommand = "Failed executing DbCommand (6ms).";
    private const string ExecutedCommand = "Executed DbCommand in 1.2ms.";
    private const string FailedSaveChanges = "An exception occurred in the database.";

    /// <summary>
    /// Runs <paramref name="log"/> against a logger built from the shipped configuration and scoped
    /// to one category, and returns what reached the collector.
    /// </summary>
    private static CollectingSink Run(string sourceContext, Action<ILogger> log)
    {
        var sink = new CollectingSink();

        log(ConfiguredLogger(sink).ForContext(Constants.SourceContextPropertyName, sourceContext));

        return sink;
    }

    private static ILogger ConfiguredLogger(CollectingSink sink) => new LoggerConfiguration()
        .AddCatalogLogging(Configuration())
        .WriteTo.Sink(sink)
        .CreateLogger();

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .Build();

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
