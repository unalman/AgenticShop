using AgenticShop.Shared.Tracing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;

namespace AgenticShop.Shared.UnitTests;

/// <summary>
/// Which requests earn a span, and that the decision is actually applied to the instrumentation
/// rather than only written down.
/// </summary>
/// <remarks>
/// The filter exists because the compose healthcheck polls <c>/health</c> on a schedule: measured in
/// a containerised Catalog, each probe emitted one access-log line and a 26-line span dump, so a
/// ten-second interval filled the log with roughly 160 lines a minute of nothing. Suppressing the
/// span costs nothing observable — the access log still records every probe with its status and
/// duration — which is why this is a filter and not a change to the exporter.
/// </remarks>
public class ServiceTracingTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public void HealthProbesAreNotTraced(string path)
    {
        IsTraced(path).Should().BeFalse(
            "a scheduled probe produces a span nobody reads, and the access log already records it");
    }

    [Theory]
    [InlineData("/api/v1/products")]
    [InlineData("/")]
    [InlineData("/scalar/v1")]
    public void OrdinaryRequestsAreTraced(string path)
    {
        IsTraced(path).Should().BeTrue("only the health endpoints are excluded");
    }

    [Fact]
    public void TheExclusionMatchesWholeSegmentsNotAPrefix()
    {
        // StartsWithSegments, not StartsWith. A future /healthz or /health-report is a real endpoint
        // whose span is worth keeping, and it would be silently dropped by a prefix match.
        IsTraced("/healthz").Should().BeTrue();
        IsTraced("/health-report").Should().BeTrue();
    }

    [Fact]
    public void TheFilterIsWiredIntoTheAspNetCoreInstrumentation()
    {
        // The predicate being correct proves nothing if AddAspNetCoreInstrumentation is called without
        // the options lambda, which is exactly the mistake a future edit makes by deleting one
        // argument. This asserts the registered options carry a filter and that it excludes /health.
        var filter = RegisteredFilter();

        filter.Should().NotBeNull("the instrumentation must be configured with the filter");
        filter!(ContextFor("/health")).Should().BeFalse();
        filter(ContextFor("/api/v1/products")).Should().BeTrue();
    }

    // --- Helpers ------------------------------------------------------------------------

    private static bool IsTraced(string path) => ServiceTracing.IsTracedRequest(ContextFor(path));

    private static HttpContext ContextFor(string path)
        => new DefaultHttpContext { Request = { Path = path } };

    /// <summary>
    /// Builds a provider and resolves <see cref="TracerProvider"/> before reading the options, because
    /// <c>WithTracing</c> defers its configuration callback until the provider is materialised — reading
    /// the options first would see the defaults and pass for the wrong reason.
    /// </summary>
    /// <remarks>
    /// No exporter is registered: <c>exportToConsole</c> is false and the configuration carries no
    /// <see cref="ServiceTracing.OtlpEndpointKey"/>, so materialising the provider performs no I/O.
    /// <para>
    /// This is the one test in the class that names an upstream type, and it is knowingly the brittle
    /// one: the options type has already been renamed once, from <c>AspNetCoreInstrumentationOptions</c>
    /// to <see cref="AspNetCoreTraceInstrumentationOptions"/>, so a future OpenTelemetry bump can break
    /// this file by renaming it again. That cost is accepted because the alternative — no assertion
    /// that the filter reaches <c>AddAspNetCoreInstrumentation</c> at all — leaves a one-argument
    /// deletion undetected, and the failure is a compile error naming the type rather than a silent
    /// gap.
    /// </para>
    /// </remarks>
    private static Func<HttpContext, bool>? RegisteredFilter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceTracing(
            new ConfigurationBuilder().Build(),
            serviceName: "AgenticShop.Tests",
            exportToConsole: false);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();

        return provider
            .GetRequiredService<IOptions<AspNetCoreTraceInstrumentationOptions>>()
            .Value
            .Filter;
    }
}
