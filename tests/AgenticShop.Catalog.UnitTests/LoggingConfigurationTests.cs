using AgenticShop.Catalog.Errors;
using AgenticShop.Shared.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace AgenticShop.Catalog.UnitTests;

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

    private static readonly string HandlerCategory = typeof(CatalogExceptionHandler).FullName!;

    [Fact]
    public void EfFailureCategoriesAreSilenced()
    {
        // EF logs both at Error and cannot know whether the failure was handled, so a routine
        // duplicate-SKU 409 used to look like an incident. Severity belongs to the handler, which is
        // the only component that knows. Warning would not do: a minimum-level override still admits
        // Error, which is the level both entries are written at.
        Run(EfUpdate, logger => logger.Error("An exception occurred in the database."))
            .Events.Should().BeEmpty();

        Run(EfCommand, logger => logger.Error("Failed executing DbCommand (6ms)."))
            .Events.Should().BeEmpty();

        // The Information entry EF writes for every successful command goes with it.
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
        // The invariant that silencing EF must not damage: a handled rejection is a Warning with no
        // exception object, an unhandled failure an Error with one.
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
