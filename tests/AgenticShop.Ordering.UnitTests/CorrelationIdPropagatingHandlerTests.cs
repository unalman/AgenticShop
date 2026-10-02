using System.Net;
using AgenticShop.Ordering.Clients;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// Outbound correlation-id propagation. Nothing in Catalog or Stock exercises this direction — both
/// are inbound-only — so it is new behaviour rather than a copied convention, and it gets its own
/// tests for that reason.
/// </summary>
/// <remarks>
/// The value is tested rather than the wiring: a handler that silently stopped forwarding would not
/// break a single other test, and the first symptom would be a trace that ends at the hop.
/// </remarks>
public class CorrelationIdPropagatingHandlerTests
{
    [Fact]
    public async Task ForwardsTheAmbientCorrelationId()
    {
        var (capturing, _) = await SendAsync("trace-across-the-hop");

        capturing.Request!.Headers
            .GetValues(CorrelationIdMiddleware.HeaderName)
            .Should().ContainSingle()
            .Which.Should().Be("trace-across-the-hop");
    }

    [Fact]
    public async Task UsesTheMiddlewareConstantForTheHeaderName()
    {
        // Not a literal. Two spellings of the same header would compile, pass every unit test, and
        // break the trace in production.
        var (capturing, _) = await SendAsync("any");

        CorrelationIdMiddleware.HeaderName.Should().Be("X-Correlation-Id");
        capturing.Request!.Headers.Should().ContainKey(CorrelationIdMiddleware.HeaderName);
    }

    [Fact]
    public async Task OmitsTheHeaderWhenThereIsNoAmbientRequest()
    {
        // A background worker, or a client called directly from a test. Minting one here would start
        // a trace that leads nowhere and could not be joined to anything.
        var (capturing, _) = await SendAsync(ambientCorrelationId: null);

        capturing.Request!.Headers.Should().NotContainKey(CorrelationIdMiddleware.HeaderName);
    }

    [Fact]
    public async Task DoesNotAddASecondHeaderWhenTheRequestAlreadyCarriesOne()
    {
        // TryAddWithoutValidation appends rather than no-ops, so without the guard a request built
        // with its own value — a default header on the client, say — would go out with two of them.
        // The handler sits in a pipeline it does not own and should not corrupt the message.
        var capturing = new CapturingHandler();
        var handler = new CorrelationIdPropagatingHandler(new Accessor("from-the-middleware"))
        {
            InnerHandler = capturing
        };

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/v1/products/1");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "from-the-caller");

        await new HttpMessageInvoker(handler).SendAsync(request, CancellationToken.None);

        capturing.Request!.Headers
            .GetValues(CorrelationIdMiddleware.HeaderName)
            .Should().BeEquivalentTo(["from-the-caller"]);
    }

    [Fact]
    public async Task LeavesTheResponseAlone()
    {
        var (_, response) = await SendAsync("any");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<(CapturingHandler Request, HttpResponseMessage Response)> SendAsync(
        string? ambientCorrelationId)
    {
        var capturing = new CapturingHandler();

        var handler = new CorrelationIdPropagatingHandler(new Accessor(ambientCorrelationId))
        {
            InnerHandler = capturing
        };

        var response = await new HttpMessageInvoker(handler).SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/v1/products/1"),
            CancellationToken.None);

        return (capturing, response);
    }

    /// <summary>
    /// Stands in for the ambient request. Only the correlation id is read, and only through the same
    /// extension method the middleware writes it with, so the two cannot disagree about where it is
    /// kept.
    /// </summary>
    private sealed class Accessor(string? correlationId) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = correlationId is null
            ? null
            : new DefaultHttpContext
            {
                TraceIdentifier = correlationId
            };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
