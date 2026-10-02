namespace AgenticShop.Ordering.Clients;

/// <summary>
/// Thrown when a downstream service could not complete a call: a 5xx, a timeout, or a transport
/// failure. Carries the service and operation as typed properties for the log line; neither reaches
/// the client, because naming our dependencies in a response body discloses internal topology.
/// </summary>
/// <remarks>
/// <para>
/// A distinct type because the handler maps it to <b>502</b>, and because the mapping must not fall
/// through to the generic 500 arm: a 500 says "this assembly has a bug", while a 502 says "we asked
/// and were let down". Operators page on different things for those two.
/// </para>
/// <para>
/// Timeouts arrive as <see cref="TaskCanceledException"/>, which the handler already has an arm for
/// — but that arm stands down only when the <i>caller</i> aborted. The clients catch it here so a
/// downstream timeout cannot be mistaken for a client hanging up, which would silently drop the
/// response instead of reporting the failure.
/// </para>
/// </remarks>
public sealed class DownstreamServiceException(
    string service,
    string operation,
    int? statusCode,
    Exception? innerException)
    : Exception(
        $"The {service} service did not complete {operation}" +
        (statusCode is null ? "." : $" (HTTP {statusCode})."),
        innerException)
{
    public string Service { get; } = service;

    public string Operation { get; } = operation;

    /// <summary>Null for a transport failure or timeout, where there was no response to read.</summary>
    public int? StatusCode { get; } = statusCode;
}
