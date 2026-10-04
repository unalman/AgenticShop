using AgenticShop.Ordering.Health;
using AgenticShop.Shared.Health;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Net;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// How a downstream probe is interpreted. This is the check's own logic, so it is tested here rather
/// than through <c>/health/ready</c>, where the only downstream the integration host can reach is one
/// that does not resolve.
/// </summary>
public class DownstreamHealthCheckTests
{
    [Fact]
    public async Task AHealthyDownstreamIsHealthy()
    {
        var result = await CheckAsync(_ => Respond(HttpStatusCode.OK));

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task ADownstreamThatReportsUnhealthyIsUnhealthy()
    {
        // Its own /health answered 503, which means its database is down — and Ordering cannot look up
        // a product or reserve stock against a service that cannot read its own data.
        var result = await CheckAsync(_ => Respond(HttpStatusCode.ServiceUnavailable));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Catalog reported 503");
    }

    [Fact]
    public async Task ADownstreamThatCannotBeReachedIsUnhealthy()
    {
        var result = await CheckAsync(_ => throw new HttpRequestException("connection refused"));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Catalog did not respond");
        result.Exception.Should().NotBeNull("the exception belongs in the log, never in the response");
    }

    [Fact]
    public async Task ADownstreamThatTimesOutIsUnhealthy()
    {
        // HttpClient reports its own timeout as a cancelled task. Without this arm the probe would
        // throw, and a thrown check is reported by the framework at its failureStatus with no
        // description of which dependency was slow.
        var result = await CheckAsync(_ => throw new TaskCanceledException());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("timed out");
    }

    [Fact]
    public async Task ACallerWhoHangsUpIsNotReportedAsADeadDependency()
    {
        // The same distinction DownstreamClient makes. Swallowing this would turn "the probe was
        // cancelled" into "Catalog is down", which is the one wrong answer a readiness check can give.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var check = new DownstreamHealthCheck(
            Client(_ => throw new TaskCanceledException()),
            new Uri("http://catalog.invalid/health"),
            "Catalog");

        var act = () => check.CheckHealthAsync(Context(), cancelled.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task TheProbeHitsTheSharedHealthPath()
    {
        // The URI is built from HealthEndpoint.Path rather than a second literal, so a probe cannot
        // drift onto a path the downstream does not expose. Asserted because the two services are in
        // different assemblies and nothing else would catch it.
        Uri? requested = null;

        await CheckAsync(request =>
        {
            requested = request.RequestUri;
            return Respond(HttpStatusCode.OK);
        });

        requested!.AbsolutePath.Should().Be(HealthEndpoint.Path);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task AnyNonSuccessStatusIsUnhealthy(HttpStatusCode status)
    {
        // Not just 503: a downstream answering 404 on /health is misconfigured, which is also "cannot
        // place an order right now".
        (await CheckAsync(_ => Respond(status))).Status.Should().Be(HealthStatus.Unhealthy);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static async Task<HealthCheckResult> CheckAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string downstream = "Catalog")
    {
        var check = new DownstreamHealthCheck(
            Client(respond),
            new Uri("http://catalog.invalid/health"),
            downstream);

        return await check.CheckHealthAsync(Context());
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(
            "Catalog",
            new NoopCheck(),
            failureStatus: null,
            tags: [HealthEndpoint.ReadyTag])
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new StubHandler(respond)) { Timeout = DownstreamHealthCheck.Timeout };

    private static HttpResponseMessage Respond(HttpStatusCode status) => new(status);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class NoopCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
