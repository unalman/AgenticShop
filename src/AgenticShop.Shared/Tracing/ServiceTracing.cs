using AgenticShop.Shared.Health;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgenticShop.Shared.Tracing;

/// <summary>
/// Registers the tracing pipeline every service shares: one span per inbound request, a resource that
/// names the service, and whichever exporters the environment asks for.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is service-specific beyond the two parameters, so it lives beside
/// <see cref="Logging.ServiceLogging"/> rather than being copied three times — the same call the
/// shared-library extraction made for the validation filter and the correlation middleware.
/// </para>
/// <para>
/// Worth being precise about what this does and does not cause. <c>Activity.Current</c> is populated
/// by ASP.NET Core's hosting layer whether or not this is called, so
/// <see cref="Middleware.CorrelationIdMiddleware"/> resolves a W3C trace id either way. What
/// registering this adds is the span tree — child spans for the request's work, recorded rather than
/// discarded — and somewhere to send it. Without it the trace id in the logs refers to a trace that
/// was never captured anywhere.
/// </para>
/// <para>
/// Deliberately not configured: <c>RecordException</c> and span enrichment. Both add detail nothing
/// has asked for yet.
/// </para>
/// <para>
/// A filter for the health endpoints <i>is</i> configured, and this is the moment the remarks on
/// this class originally deferred it to: <c>/health</c> is now called on a schedule, by the compose
/// healthcheck, so its spans are noise rather than signal. Measured in a containerised Catalog, one
/// probe produced a single access-log line and a 26-line span dump — at a ten-second interval that is
/// roughly 160 lines a minute of nothing, and it buries the requests worth reading. The access-log
/// line survives, so a probe is still recorded with its status and duration; what goes is the span.
/// See <see cref="IsTracedRequest"/>.
/// </para>
/// </remarks>
public static class ServiceTracing
{
    /// <summary>
    /// Read as a configuration key rather than through <c>Environment.GetEnvironmentVariable</c> so
    /// that it is spelled the way the OpenTelemetry specification spells it — a deployment sets
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and the host's environment-variable configuration provider
    /// surfaces it here under the same name.
    /// </summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <param name="serviceName">
    /// The <c>service.name</c> resource attribute. Without it all three hosts export identical
    /// unnamed spans and a trace view cannot tell them apart.
    /// </param>
    /// <param name="exportToConsole">
    /// Writes spans to stdout. Intended for Development, where it makes the span tree visible with no
    /// collector running; in a production log pipeline it would duplicate every span into the log
    /// stream.
    /// </param>
    /// <param name="instrumentHttpClient">
    /// Adds a span per outbound call and propagates <c>traceparent</c> onto it. True for Ordering,
    /// the only service that makes outbound calls — the other two are leaf services and their
    /// boundary is asserted by <c>LeafServiceBoundaryTests</c>, so they have no client to instrument.
    /// </param>
    public static IServiceCollection AddServiceTracing(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        bool exportToConsole,
        bool instrumentHttpClient = false)
    {
        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing.ConfigureResource(resource => resource.AddService(serviceName));

            tracing.AddAspNetCoreInstrumentation(options => options.Filter = IsTracedRequest);

            if (instrumentHttpClient)
            {
                tracing.AddHttpClientInstrumentation();
            }

            if (exportToConsole)
            {
                tracing.AddConsoleExporter();
            }

            // Gated on an endpoint being configured rather than added unconditionally. The OTLP
            // exporter defaults to localhost:4317 and would otherwise log a failed export for every
            // batch on a machine with no collector, which reads like an outage and is not one.
            if (!string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]))
            {
                tracing.AddOtlpExporter();
            }
        });

        return services;
    }

    /// <summary>
    /// The instrumentation filter: false suppresses the span for that request. Excludes both health
    /// endpoints, because a scheduled probe produces a span nobody reads — <see cref="HealthEndpoint.ReadyPath"/>
    /// is covered by the same test, since <c>StartsWithSegments</c> matches on whole path segments
    /// and so takes in everything beneath <see cref="HealthEndpoint.Path"/>.
    /// </summary>
    /// <remarks>
    /// Segment matching is what makes this safe to widen later and precise now: <c>/health</c> and
    /// <c>/health/ready</c> are excluded while <c>/healthz</c>, or any future endpoint that merely
    /// begins with those characters, is not. Public rather than private so that the decision — which
    /// paths are worth a span — is asserted directly in <c>AgenticShop.Shared.UnitTests</c> instead of
    /// only through the instrumentation's options.
    /// </remarks>
    public static bool IsTracedRequest(HttpContext context)
        => !context.Request.Path.StartsWithSegments(HealthEndpoint.Path);
}
