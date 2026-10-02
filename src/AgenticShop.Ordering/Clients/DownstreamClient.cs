namespace AgenticShop.Ordering.Clients;

/// <summary>
/// The plumbing both typed clients share: send, and classify a transport failure before it can be
/// misread by anything upstream.
/// </summary>
/// <remarks>
/// This is a private base inside one service, not a shared abstraction across services — the
/// duplication this project accepts by decision is the cross-service kind. Two clients making the
/// same three distinctions is worth stating once, because getting the timeout case wrong is silent:
/// an uncaught <see cref="TaskCanceledException"/> would reach the exception handler's cancellation
/// arm, which stands down and returns the request to the framework, so a downstream timeout would
/// produce no response at all instead of a 502.
/// </remarks>
public abstract class DownstreamClient(HttpClient http, string service)
{
    /// <summary>The service name as it appears in logs. Never returned to a client.</summary>
    protected string Service { get; } = service;

    protected async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new DownstreamServiceException(Service, operation, statusCode: null, exception);
        }
        // HttpClient reports its own timeout as TaskCanceledException. The guard on the token is
        // what separates "Stock took too long" from "our caller hung up", which the handler
        // deliberately does not treat as an error.
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownstreamServiceException(Service, operation, statusCode: null, exception);
        }
    }

    /// <summary>
    /// A 5xx is the dependency's fault and is reported as one, so the caller answers 502. Any
    /// other status this client does not implement is <b>our</b> fault — we sent something the
    /// contract does not allow — so it becomes an <see cref="InvalidOperationException"/> and the
    /// handler's default 500 arm. Answering 502 for that would blame the other service for our bug,
    /// which is the same inversion Stock refuses when it reports a CHECK violation as a 500.
    /// </summary>
    protected static Exception Unexpected(string service, string operation, HttpResponseMessage response)
        => (int)response.StatusCode >= StatusCodes.Status500InternalServerError
            ? new DownstreamServiceException(service, operation, (int)response.StatusCode, innerException: null)
            : new InvalidOperationException(
                $"{service} answered {operation} with HTTP {(int)response.StatusCode}, " +
                "which this client does not implement.");

    /// <summary>
    /// A 2xx whose body cannot be read is a contract breach, not a transient failure, so retrying
    /// or reconciling would not help. Reported as our fault.
    /// </summary>
    protected static Exception MalformedBody(string service, string operation)
        => new InvalidOperationException(
            $"{service} returned a success response to {operation} that this client could not read.");
}
