using System.Net;
using System.Text;
using AgenticShop.Ordering.Clients;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// The resilience pipeline on the typed clients: what gets retried, what must not be, and how a
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
/// The integration suite cannot cover this. It replaces <c>ICatalogClient</c> and <c>IStockClient</c>
/// with fakes, which sits above the HTTP pipeline entirely — so it still proves the wiring in
/// <c>Program.cs</c> builds a host, and nothing else here.
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
    public async Task DoesNotRetryACallMarkedNotRetryable()
    {
        // The reserve case. A retry here is not merely wasteful: if the first attempt committed and
        // only its response was lost, Stock answers the retry 409 from its unique index, the client
        // reads that as "cannot be held", and the hold from the first attempt is stranded while the
        // order is reported out of stock.
        var downstream = new ScriptedHandler(_ => Respond(HttpStatusCode.ServiceUnavailable));

        var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/stock/1/reservations");
        request.Options.Set(DownstreamResilience.NotRetryable, true);

        await SendAsync(downstream, request);

        downstream.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task RetriesTheSameFailureWhenTheCallIsNotMarked()
    {
        // The control for the test above. Without it, "the marked call was not retried" would also
        // pass if retry were broken or never wired, which is the failure this policy exists to avoid.
        var downstream = new ScriptedHandler(_ => Respond(HttpStatusCode.ServiceUnavailable));

        await SendAsync(downstream, new HttpRequestMessage(HttpMethod.Post, "api/v1/reservations/1/confirm"));

        downstream.Attempts.Should().Be(1 + DownstreamResilience.MaxRetryAttempts);
    }

    [Fact]
    public async Task ReserveMarksItsRequestAsNotRetryable()
    {
        // The seam the two tests above cannot see. Deleting one line in StockClient would leave the
        // pipeline correct and the reserve path unsafe, with every other test still green.
        var downstream = new ScriptedHandler(_ => Respond(
            HttpStatusCode.Created,
            """{"id":"4c1e7a5a-2b0b-4b1a-9a3a-2f0d5c9b8a71","productId":"8f1b0a63-6b6a-4d2a-9a10-0b6d2c4f7a11","quantity":2,"status":"Pending"}"""));

        var client = new StockClient(CreateBareClient(downstream));

        await client.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), 2, CancellationToken.None);

        downstream.LastRequest!.Options
            .TryGetValue(DownstreamResilience.NotRetryable, out var marked)
            .Should().BeTrue("ReserveAsync must mark the request");

        marked.Should().BeTrue();
    }

    [Fact]
    public async Task PreservesTheCorrelationIdOnEveryAttempt()
    {
        var downstream = new ScriptedHandler(
            _ => Respond(HttpStatusCode.ServiceUnavailable),
            _ => Respond(HttpStatusCode.OK));

        var services = new ServiceCollection();

        services
            .AddHttpClient("under-test", client => client.BaseAddress = new Uri("http://downstream.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => downstream)
            // Same order as Program.cs: correlation outermost, so it runs once and the header is
            // already on the message before the pipeline retries it.
            .AddHttpMessageHandler(() => new CorrelationIdPropagatingHandler(new Accessor("trace-across-retries")))
            .AddDownstreamResilience();

        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("under-test");

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/v1/products/1"));

        downstream.Attempts.Should().Be(2);

        foreach (var attempt in downstream.Requests)
        {
            attempt.Headers.GetValues(CorrelationIdMiddleware.HeaderName)
                .Should()
                .BeEquivalentTo(["trace-across-retries"], "every attempt must carry the one ambient id");
        }
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

    /// <summary>Stands in for the ambient request, as in the correlation-handler tests.</summary>
    private sealed class Accessor(string correlationId) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext
        {
            TraceIdentifier = correlationId
        };
    }
}
