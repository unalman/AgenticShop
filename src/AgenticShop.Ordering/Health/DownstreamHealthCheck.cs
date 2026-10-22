using AgenticShop.Shared.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgenticShop.Ordering.Health;

/// <summary>
/// Probes a downstream service's own <c>/health</c> and reports whether Ordering can currently do its
/// job. Readiness-only: it is tagged <see cref="HealthEndpoint.ReadyTag"/>, so it runs on
/// <c>/health/ready</c> and never on <c>/health</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not on <c>/health</c>.</b> A liveness endpoint that depends on another service turns
/// one outage into two: Stock goes down, Ordering starts reporting Unhealthy, and an orchestrator
/// pulls Ordering out of rotation even though it can still serve <c>GET /orders/{id}</c> and would
/// honestly refuse placements with a 502. Keeping the downstream probe on a separate endpoint is what
/// stops that, and it is the standard liveness/readiness split.
/// </para>
/// <para>
/// <b>Why it does not use <c>ICatalogClient</c> or <c>IStockClient</c>.</b> Those typed clients run
/// through the resilience pipeline, whose total timeout is 35 seconds. A readiness probe that waited
/// 35 seconds would report a *slow* Catalog as a dead Ordering. This uses its own named client with a
/// <see cref="Timeout"/> measured in seconds and no retry — a probe should fail fast and say so, not
/// absorb the fault it is trying to report.
/// </para>
/// <para>
/// <b>Why <see cref="HealthStatus.Unhealthy"/> rather than Degraded.</b> Degraded maps to HTTP 200 by
/// default, so a consumer would keep routing placements that cannot succeed. Unhealthy maps to 503,
/// which is the honest answer to "can you place an order right now".
/// </para>
/// <para>
/// Probing the downstream's <c>/health</c> means this check also fails when the downstream's
/// <i>database</i> is down, which is correct: Ordering cannot look up a product or reserve stock
/// against a service that cannot read its own data.
/// </para>
/// <para>
/// Every description is a string this class builds, never an exception message, so descriptions stay
/// safe to expose even where <c>HealthEndpoint</c> enables them — and they name the downstream, which
/// is the one thing a bare "Unhealthy" cannot tell a caller.
/// </para>
/// </remarks>
/// <param name="http">The shared probe client; see <see cref="DownstreamHealthChecks"/>.</param>
/// <param name="healthUri">
/// Resolves the downstream's full <c>/health</c> address, and deliberately a factory rather than a
/// <see cref="Uri"/>: the target has to be read from configuration at probe time, not captured at
/// registration. <c>WebApplicationFactory</c> applies its configuration override only once
/// <c>Program.cs</c> reaches <c>Build()</c>, so a <c>Uri</c> built during registration freezes
/// <c>appsettings.Development.json</c> — the same trap <c>ARCHITECTURE.md</c> §5.4 records for the
/// connection string. This is measured, not theorised: with the containerised services running, an
/// eagerly built target made the test host probe the developer's own <c>localhost:5081</c> and
/// <c>localhost:5082</c>, both answered 200, and <c>/health/ready</c> reported Healthy instead of
/// Unhealthy. A missing or malformed value still fails fast in a real deployment, because
/// <c>Program.cs</c> validates both keys after <c>Build()</c>.
/// </param>
/// <param name="downstream">
/// The downstream's name, used as the subject of every description this check writes.
/// </param>
public sealed class DownstreamHealthCheck(HttpClient http, Func<Uri> healthUri, string downstream) : IHealthCheck
{
    /// <summary>
    /// Bounds one probe. Two seconds is far above a loopback round trip and far below the 35-second
    /// resilience total, so a hung downstream makes <c>/health/ready</c> slow but not unusable. Set on
    /// the shared probe client at registration; see <see cref="DownstreamHealthChecks"/>.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Resolved outside the try: a configuration failure is not a probe outcome, and letting it
        // propagate reports the check as Unhealthy with the exception attached rather than as
        // "Catalog did not respond".
        var target = healthUri();

        try
        {
            using var response = await http.GetAsync(target, cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{downstream} reported {(int)response.StatusCode}");
        }
        catch (HttpRequestException exception)
        {
            // No response at all: refused, DNS failure, or the port is not listening. The exception is
            // attached so the framework's own Error line carries the cause; the response writer never
            // reads it.
            return HealthCheckResult.Unhealthy($"{downstream} did not respond", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's timeout surfaces as a cancelled task. The guard keeps a caller who hung up
            // from being reported as a dead dependency — the same distinction DownstreamClient makes.
            return HealthCheckResult.Unhealthy(
                $"{downstream} timed out after {Timeout.TotalSeconds:0.#}s",
                exception);
        }
    }
}

/// <summary>
/// Registration for <see cref="DownstreamHealthCheck"/>, kept beside it because the two are one idea:
/// a check, and the tag that keeps it off the liveness endpoint.
/// </summary>
public static class DownstreamHealthChecks
{
    /// <param name="downstream">
    /// Used as both the check's entry name and the subject of its descriptions, so it should be the
    /// service's name as the rest of this service refers to it.
    /// </param>
    /// <param name="baseUrl">
    /// Resolves the downstream's configured base address. A factory for the reason
    /// <see cref="DownstreamHealthCheck"/> gives: registration runs before a test host's
    /// configuration override exists, so reading the value here would freeze it.
    /// <see cref="HealthEndpoint.Path"/> is appended to it — and that constant is shared precisely so
    /// this probe hits the path the downstream actually exposes, rather than a second string that
    /// could drift from it.
    /// </param>
    /// <param name="probeClient">
    /// Shared by every downstream check, and deliberately not an <c>IHttpClientFactory</c> named
    /// client: <c>AddCheck</c> has no overload that resolves an <c>IHealthCheck</c> from DI, so the
    /// instance has to exist at registration time. Resolving the factory inside the check instead
    /// would mean injecting <c>IServiceProvider</c> — a service locator, to save one object.
    /// </param>
    public static IHealthChecksBuilder AddDownstreamCheck(
        this IHealthChecksBuilder builder,
        string downstream,
        Func<Uri> baseUrl,
        HttpClient probeClient)
        => builder.AddCheck(
            downstream,
            new DownstreamHealthCheck(
                probeClient,
                () => new Uri(baseUrl(), HealthEndpoint.Path),
                downstream),
            tags: [HealthEndpoint.ReadyTag]);
}
