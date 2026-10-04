using AgenticShop.Shared.Logging;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AgenticShop.Shared.UnitTests;

/// <summary>
/// The shared logging policy: what the access log is allowed to claim, and that the correlation id
/// becomes ambient for everything written inside the middleware.
/// </summary>
/// <remarks>
/// Each service's own level overrides are asserted in that service's unit tests, from its own
/// <c>appsettings.json</c>. What is here is specified once because it is shared once.
/// </remarks>
public class ServiceLoggingTests
{
    [Fact]
    public void AccessLoggingMakesNoSeverityClaim()
    {
        // The library default returns Error for a status above 499 or a non-null exception. That
        // would put a second Error beside the one the exception handler already writes — and the
        // handler is the component that classified the failure and chose the status, so its level is
        // the authoritative one. Verified against a real dependency failure in Ordering, where the
        // default produced three Error lines for a single classified 502.
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status502BadGateway;

        var level = ServiceLogging.RequestLogLevel(
            context,
            elapsedMs: 12.5,
            new InvalidOperationException("downstream failed"));

        level.Should().Be(LogEventLevel.Information);
    }

    [Fact]
    public async Task TheMiddlewareMakesTheIdAmbientForLinesWrittenInsideIt()
    {
        // This is what makes "written to every log line for the request" true rather than
        // aspirational. EF Core's lines carry no correlation id of their own, and neither does the
        // request logger's completion event; both pick it up from the ambient context instead.
        //
        // The logger is built through AddServiceLogging rather than configured by hand, which also
        // makes this the only assertion keeping .Enrich.FromLogContext() honest. Delete that call and
        // the middleware still pushes, the property simply never reaches the event, and every other
        // test here still passes — because the one below builds its own enrichment. An empty
        // configuration is enough: there is no Serilog section to read, and the level defaults to
        // Information, which is what this logs at.
        var sink = new CollectingSink();

        var logger = new LoggerConfiguration()
            .AddServiceLogging(new ConfigurationBuilder().Build())
            .WriteTo.Sink(sink)
            .CreateLogger();

        var context = new DefaultHttpContext();

        // No inbound header is set on purpose. X-Correlation-Id is response-only now, so whatever the
        // middleware resolves is what must appear in the log — asserting a specific inbound value here
        // would be testing a contract that no longer exists. The resolution itself is covered by
        // CorrelationIdMiddlewareTests; the invariant this test owns is that the two agree.
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            logger.Information("written from inside the pipeline");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        var logEvent = sink.Events.Should().ContainSingle().Subject;

        logEvent.Properties.Should()
            .ContainKey(ServiceLogging.CorrelationIdProperty, "the push is what puts it there");

        logEvent.Properties[ServiceLogging.CorrelationIdProperty]
            .ToString()
            .Should()
            .Contain(context.GetCorrelationId(), "a log line must be joinable to its request");
    }

    [Fact]
    public async Task TheAmbientIdIsPoppedWhenTheRequestEnds()
    {
        // A push that is never popped leaks into unrelated work on the same async context, which
        // would attribute one request's log lines to another's id.
        var sink = new CollectingSink();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var context = new DefaultHttpContext();

        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        logger.Information("written after the request completed");

        sink.Events.Should().ContainSingle()
            .Which.Properties.Should()
            .NotContainKey(ServiceLogging.CorrelationIdProperty);
    }

    [Fact]
    public void TheCorrelationPropertyMatchesTheHandlersPlaceholder()
    {
        // The shared handler hardcodes {CorrelationId} in its message templates. If this constant
        // drifts from it, handler lines and EF Core's lines would carry the same value under two
        // different property names.
        ServiceLogging.CorrelationIdProperty.Should().Be("CorrelationId");

        ServiceLogging.ConsoleOutputTemplate
            .Should()
            .Contain("{" + ServiceLogging.CorrelationIdProperty, "or the template would render it empty");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
