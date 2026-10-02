using AgenticShop.Ordering.Middleware;

namespace AgenticShop.Ordering.Clients;

/// <summary>
/// Forwards the ambient request's correlation id onto every outbound call, so one client request
/// produces one id across Ordering, Catalog and Stock.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is the first service in the repository to make an outbound call, so this is the first
/// outbound direction of the correlation id — Catalog's and Stock's copies of the middleware are
/// inbound-only. Without it a trace breaks at the first hop and a 502 from here cannot be tied to
/// the downstream log line that explains it.
/// </para>
/// <para>
/// Reads the id through <see cref="IHttpContextAccessor"/> rather than receiving it as a parameter,
/// so no client method has to carry a tracing concern in its signature. The value is the one
/// <see cref="CorrelationIdMiddleware"/> already sanitised, so it is safe to put on a header.
/// </para>
/// <para>
/// The header is omitted when there is no ambient request — a background worker, or a test calling
/// a client directly. Minting one here would start a trace that leads nowhere; Phase 1 derives the
/// id from <c>Activity.Current</c> instead, which covers both directions properly.
/// </para>
/// </remarks>
public sealed class CorrelationIdPropagatingHandler(IHttpContextAccessor httpContextAccessor)
    : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?.GetCorrelationId();

        // TryAddWithoutValidation appends to a header that is already there rather than leaving it
        // alone, so the check is what stops a request built with its own value — a default header on
        // the client, say — going out with two of them. A DelegatingHandler sits in a pipeline it
        // does not own and should not corrupt a message it did not create.
        if (correlationId is not null && !request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
