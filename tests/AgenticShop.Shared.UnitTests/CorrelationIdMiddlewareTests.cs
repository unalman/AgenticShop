using AgenticShop.Shared.Logging;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;

namespace AgenticShop.Shared.UnitTests;

/// <summary>
/// How the correlation id is resolved. The id is the ambient W3C trace id, so one value ties
/// together the log lines, the response header, the ProblemDetails body and the span tree.
/// </summary>
/// <remarks>
/// The response-header echo itself is asserted in the integration suites against a real Kestrel,
/// because <c>DefaultHttpContext</c> never runs the <c>OnStarting</c> callback that writes it. What
/// is tested here is the resolution — which is where the contract change lives.
/// </remarks>
public class CorrelationIdMiddlewareTests
{
    /// <summary>A W3C trace id, which is what the middleware now emits in every case.</summary>
    private const string TraceIdPattern = "^[0-9a-f]{32}$";

    private const string InboundTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    [Fact]
    public async Task TheIdIsTheAmbientTraceId()
    {
        using var activity = new Activity("test-request").Start();

        var context = await InvokeAsync();

        context.TraceIdentifier.Should().Be(activity.TraceId.ToString());
        context.Items[CorrelationIdMiddleware.HeaderName].Should().Be(activity.TraceId.ToString());
    }

    [Fact]
    public async Task ACallerWhoJoinsAnExistingTraceGetsThatTraceIdBack()
    {
        // The property that makes this one concept rather than two: when a caller arrives under an
        // existing trace — in practice by sending traceparent, which ASP.NET Core turns into the
        // parent of the request's activity — the id in the logs and the response is the id a
        // collector indexes that trace under, not a value this service invented beside it.
        using var activity = new Activity("test-request")
            .SetParentId(
                ActivityTraceId.CreateFromString(InboundTraceId.AsSpan()),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.None)
            .Start();

        (await InvokeAsync()).TraceIdentifier.Should().Be(InboundTraceId);
    }

    [Theory]
    [InlineData("correlation-abc-123")]
    [InlineData("stock-smoke")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("line\nbreak")]
    [InlineData("emoji-😀")]
    public async Task AnInboundCorrelationIdHeaderIsIgnoredWhateverItContains(string inbound)
    {
        // The header used to be adopted, which is why it needed a charset-and-length guard: arbitrary
        // caller text was headed for logs and response headers. It is response-only now, so the guard
        // is gone rather than relaxed — and the reason that is safe is exactly what this asserts.
        // Hostile input cannot reach a log line because no inbound value is ever used.
        var context = await InvokeAsync((CorrelationIdMiddleware.HeaderName, inbound));

        context.TraceIdentifier.Should().MatchRegex(TraceIdPattern);
        context.TraceIdentifier.Should().NotBe(inbound);
        context.Items[CorrelationIdMiddleware.HeaderName].Should().NotBe(inbound);
    }

    [Fact]
    public async Task TheIdIsStillAMinted32HexValueWhenThereIsNoActivity()
    {
        // Not reachable through a real request — ASP.NET Core's hosting layer starts an activity for
        // every one, with or without OpenTelemetry registered — but TraceIdentifier cannot be null.
        Activity.Current.Should().BeNull("this test must not run inside an ambient activity");

        (await InvokeAsync()).TraceIdentifier.Should().MatchRegex(TraceIdPattern);
    }

    [Fact]
    public void MintedIdsAreDistinct()
        => CorrelationIdMiddleware.Mint().Should().NotBe(CorrelationIdMiddleware.Mint());

    [Fact]
    public async Task TheResolvedIdIsAmbientForLogLinesWrittenDuringTheRequest()
    {
        // The Serilog property and the correlation id must be the same value, or a log line cannot be
        // joined to the trace it belongs to.
        var sink = new CollectingSink();

        var logger = new LoggerConfiguration()
            .AddServiceLogging(new ConfigurationBuilder().Build())
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var activity = new Activity("test-request").Start();

        await new CorrelationIdMiddleware(_ =>
        {
            logger.Information("written during the request");
            return Task.CompletedTask;
        }).InvokeAsync(new DefaultHttpContext());

        sink.Events.Should().ContainSingle()
            .Which.Properties[ServiceLogging.CorrelationIdProperty]
            .ToString()
            .Should()
            .Contain(activity.TraceId.ToString());
    }

    [Fact]
    public void FromCurrentTraceIsNullOutsideARequest()
    {
        Activity.Current.Should().BeNull();

        CorrelationIdMiddleware.FromCurrentTrace().Should().BeNull();
    }

    // --- Helpers ------------------------------------------------------------------------

    private static async Task<HttpContext> InvokeAsync(params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();

        foreach (var (name, value) in headers)
        {
            // TryAddWithoutValidation, because the point is sending values a well-behaved client
            // would never produce.
            context.Request.Headers.TryAdd(name, value);
        }

        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        return context;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
