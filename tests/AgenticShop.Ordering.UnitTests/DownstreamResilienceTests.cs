using System.Net;
using System.Text;
using AgenticShop.Ordering.Clients;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// The resilience pipeline on the typed client: what gets retried, what does not, and how a
/// pipeline-level failure is still classified as a dependency failure.
/// </summary>
/// <remarks>
/// <para>
/// These run against the real <see cref="DownstreamResilience.AddDownstreamResilience"/>
/// registration rather than a re-created pipeline, so the values asserted are the ones production
/// uses. The retry delay is the cost of that: a test which exhausts its retries spends about a
/// second.
/// </para>
/// <para>
/// The integration suite cannot cover this. It replaces <c>ICatalogClient</c> with a fake, which sits
/// above the HTTP pipeline entirely — so it still proves the wiring in <c>Program.cs</c> builds a
/// host, and nothing else here.
/// </para>
/// <para>
/// Two tests that used to live here went with code rather than with coverage. One asserted that every
/// retry attempt carried the ambient correlation id; it went with
/// <c>CorrelationIdPropagatingHandler</c>, because trace context on an outbound call is now the
/// HttpClient instrumentation's job and testing it here would mean testing OpenTelemetry. The other
/// asserted the per-request retry exemption; it went with the synchronous reserve call that was the
/// only thing needing one. The reasoning behind that exemption still binds a future outbound write
/// and is recorded in <see cref="DownstreamResilience"/> and <c>docs/DECISIONS.md</c> → O18.
/// </para>
/// </remarks>
public class DownstreamResilienceTests
{
    [Fact]
    public async Task RetriesATransientServerFailure()
    {
        var downstream = new ScriptedHandler(
            _ => Respond(HttpStatusCode.ServiceUnavailable),
            _ => Respond(HttpStatusCode.ServiceUnavailable),
            _ => Respond(HttpStatusCode.OK));

        var response = await SendAsync(downstream, new HttpRequestMessage(HttpMethod.Get, "api/v1/products/1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task StopsRetryingAtTheConfiguredMaximum()
    {
        var downstream = new ScriptedHandler(_ => Respond(HttpStatusCode.ServiceUnavailable));

        await SendAsync(downstream, new HttpRequestMessage(HttpMethod.Get, "api/v1/products/1"));

        // Asserted against the constant rather than a literal 3, so changing the policy changes the
        // expectation instead of silently invalidating it.
        downstream.Attempts.Should().Be(1 + DownstreamResilience.MaxRetryAttempts);
    }

    [Fact]
    public async Task RetriesATransportFailure()
    {
        var downstream = new ScriptedHandler(
            _ => throw new HttpRequestException("connection refused"),
            _ => Respond(HttpStatusCode.OK));

        var response = await SendAsync(downstream, new HttpRequestMessage(HttpMethod.Get, "api/v1/products/1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.Attempts.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task DoesNotRetryANormalClientError(HttpStatusCode status)
    {
        // A 4xx the caller caused is not going to become a 2xx by being asked again, and retrying it
        // turns one bad request into three. Stock's 409 in particular is a value the placement path
        // acts on, not a transient fault.
        var downstream = new ScriptedHandler(_ => Respond(status));

        var response = await SendAsync(downstream, new HttpRequestMessage(HttpMethod.Get, "api/v1/products/1"));

        response.StatusCode.Should().Be(status);
        downstream.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task ClassifiesAPipelineTimeoutAsADependencyFailure()
    {
        // The pipeline reports its own timeouts as TimeoutRejectedException, not the
        // TaskCanceledException HttpClient uses. Unclassified it would reach the handler's default
        // arm and answer 500, reporting a dependency that failed as a bug in this service.
        var downstream = new ScriptedHandler(_ => throw new TimeoutRejectedException());

        var act = () => new CatalogClient(CreateBareClient(downstream))
            .GetProductAsync(Guid.NewGuid(), CancellationToken.None);

        (await act.Should().ThrowAsync<DownstreamServiceException>())
            .Which.Service.Should().Be("Catalog");
    }

    [Fact]
    public async Task ClassifiesAnOpenCircuitAsADependencyFailure()
    {
        // An open circuit means the call was never attempted. That is still "the dependency is not
        // answering" from the caller's side, so it is a 502 and not a 500.
        var downstream = new ScriptedHandler(_ => throw new BrokenCircuitException());

        var act = () => new CatalogClient(CreateBareClient(downstream))
            .GetProductAsync(Guid.NewGuid(), CancellationToken.None);

        (await act.Should().ThrowAsync<DownstreamServiceException>())
            .Which.Service.Should().Be("Catalog");
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ScriptedHandler downstream,
        HttpRequestMessage request)
    {
        var services = new ServiceCollection();

        services
            .AddHttpClient("under-test", client => client.BaseAddress = new Uri("http://downstream.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => downstream)
            .AddDownstreamResilience();

        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("under-test");

        return await client.SendAsync(request);
    }

    /// <summary>
    /// A client with no resilience pipeline, for the tests that assert how a pipeline failure is
    /// classified once it reaches <see cref="DownstreamClient"/>.
    /// </summary>
    private static HttpClient CreateBareClient(HttpMessageHandler downstream)
        => new(downstream) { BaseAddress = new Uri("http://downstream.test/") };

    private static HttpResponseMessage Respond(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status);

        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return response;
    }

    /// <summary>Answers from a script, repeating its last entry once the script runs out.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
        : HttpMessageHandler
    {
        private int served;

        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpRequestMessage? LastRequest => Requests.Count == 0 ? null : Requests[^1];

        public int Attempts => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(script[Math.Min(served++, script.Length - 1)](request));
        }
    }
}
