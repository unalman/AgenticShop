using AgenticShop.Ordering.Errors;
using AgenticShop.Shared.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// That <b>this service's</b> shipped <c>appsettings.json</c> produces the logging behaviour the
/// error-handling design depends on. The shared policy is specified once, in
/// <c>AgenticShop.Shared.UnitTests</c>; what is asserted here is the configuration file, which is
/// per service and can be wrong on its own.
/// </summary>
/// <remarks>
/// Built from the real file, copied to this project's output, rather than a hand-written mirror of
/// it. That distinction has already paid for itself once: an invalid level name sat in the shipped
/// file while a mirrored copy failed the test instead. An unrecognised level name is not a warning
/// either — Serilog parses it as a level switch and throws while the host builds, so this file
/// failing to construct is the same failure as the service failing to start.
/// </remarks>
public class LoggingConfigurationTests
{
    private const string EfUpdate = "Microsoft.EntityFrameworkCore.Update";
    private const string EfCommand = "Microsoft.EntityFrameworkCore.Database.Command";
    private const string EfMigrations = "Microsoft.EntityFrameworkCore.Migrations";
    private const string HttpClient = "System.Net.Http.HttpClient";
    private const string Resilience = "Microsoft.Extensions.Http.Resilience";

    private static readonly string HandlerCategory = typeof(OrderingExceptionHandler).FullName!;

    [Fact]
    public void EfFailureCategoriesAreSilenced()
    {
        // EF logs both at Error and cannot know whether the failure was handled. Ordering's handled
        // cases include a failed idempotency-key claim and an order that was already placed, so
        // without the overrides a retried POST /orders would look like an incident. Warning would not
        // do: a minimum-level override still admits Error, the level both are written at.
        Run(EfUpdate, logger => logger.Error("An exception occurred in the database."))
            .Events.Should().BeEmpty();

        Run(EfCommand, logger => logger.Error("Failed executing DbCommand (6ms)."))
            .Events.Should().BeEmpty();

        Run(EfCommand, logger => logger.Information("Executed DbCommand in 1.2ms."))
            .Events.Should().BeEmpty();
    }

    [Fact]
    public void OrdinaryEfCategoriesStillLog()
    {
        // Guards the two overrides above being read as "EF Core is silenced". They are per category,
        // so migrations still report what they applied.
        Run(EfMigrations, logger => logger.Information("Applying migrations."))
            .Events.Should().ContainSingle()
            .Which.Level.Should().Be(LogEventLevel.Information);
    }

    [Fact]
    public void TheHandlerSeveritySplitSurvives()
    {
        // The invariant that silencing EF must not damage: a 4xx is a Warning with no exception
        // object, a 5xx an Error with one. Note the split is by *status*, not by
        // handled-versus-unhandled — Ordering's classified 502 for a failed dependency is a 5xx and
        // logs at Error, which is correct: a dependency being down is worth paging on. What must not
        // happen is a second Error from the access log beside it, which is what
        // ServiceLogging.RequestLogLevel prevents and what AgenticShop.Shared.UnitTests asserts.
        var sink = Run(HandlerCategory, logger =>
        {
            logger.Warning("Request rejected with {StatusCode}.", 409);
            logger.Error(new InvalidOperationException("bug"), "Unhandled exception.");
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
    public void DownstreamFailuresAreLoggedOnceNotTwice()
    {
        // Ordering is the only service that makes outbound calls, so it is the only one where the
        // HTTP stack and the handler would both report the same failure. The stack's per-request
        // Information chatter is dropped; its Error is kept, and so is the handler's.
        Run(HttpClient, logger => logger.Information("Sending HTTP request POST http://stock/api/v1/..."))
            .Events.Should().BeEmpty();

        Run(HttpClient, logger => logger.Error(new HttpRequestException("refused"), "Request failed."))
            .Events.Should().ContainSingle()
            .Which.Level.Should().Be(LogEventLevel.Error);
    }

    [Fact]
    public void TheResiliencePipelineIsNotSilenced()
    {
        // Deliberately left at the default. A retry that recovered and a circuit that opened are both
        // worth seeing, and neither is an error — but they are invisible if this category is ever
        // folded into a blanket "Microsoft.Extensions": "Warning" override. This asserts the category
        // is not silenced; it makes no claim about which level Polly chooses to log at.
        Run(Resilience, logger => logger.Information("Retry attempt 1."))
            .Events.Should().ContainSingle();
    }

    // --- Helpers ------------------------------------------------------------------------

    private static CollectingSink Run(string sourceContext, Action<ILogger> log)
    {
        var sink = new CollectingSink();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        log(new LoggerConfiguration()
            .AddServiceLogging(configuration)
            .WriteTo.Sink(sink)
            .CreateLogger()
            .ForContext(Constants.SourceContextPropertyName, sourceContext));

        return sink;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
