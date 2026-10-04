using AgenticShop.Shared.Logging;
using Serilog.Context;

namespace AgenticShop.Shared.Middleware;

/// <summary>
/// Accepts an inbound X-Correlation-Id or mints one, echoes it on the response, and makes it ambient
/// for every log line written while handling the request. Adopting an inbound value is what lets a
/// single request be traced across Ordering, Catalog and Stock.
/// </summary>
/// <remarks>
/// Registered first in the pipeline, before <c>UseExceptionHandler</c>, so a failure in any later
/// component still carries the id. Inbound-only for Catalog and Stock; Ordering pairs this with
/// its own <c>CorrelationIdPropagatingHandler</c>, which forwards the value resolved here onto
/// every downstream call. That outbound direction is a service concern and stays in the service.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// Room for a GUID (32 chars), a W3C trace id, or a prefixed token, while still
    /// bounding how much untrusted text a caller can push into logs and response headers.
    /// </summary>
    public const int MaxLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context.Request.Headers[HeaderName].FirstOrDefault());

        context.TraceIdentifier = correlationId;
        context.Items[HeaderName] = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // Pushed here rather than in a second middleware so the scope necessarily wraps everything
        // downstream. That matters for request logging, whose completion event is written after the
        // inner pipeline returns: a push registered *after* it would already be popped by then and
        // the id would be missing from exactly the line that summarises the request. Inert where
        // Serilog is not the active provider, and EF Core's own lines carry no id of their own, so
        // this is what makes "written to every log line for the request" true.
        using (LogContext.PushProperty(ServiceLogging.CorrelationIdProperty, correlationId))
        {
            await next(context);
        }
    }

    /// <summary>
    /// The inbound value ends up in response headers, in the ProblemDetails body and in
    /// every log line for the request, so it is treated as untrusted input.
    /// </summary>
    public static string Resolve(string? inbound)
        => IsAcceptable(inbound) ? inbound! : Mint();

    public static string Mint() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// An unacceptable id is replaced rather than rejected: a malformed correlation id does
    /// not make the underlying request invalid, and refusing the call would turn a tracing
    /// concern into an availability one.
    /// </summary>
    public static bool IsAcceptable(string? value)
        => value is { Length: > 0 and <= MaxLength } && value.All(IsAllowedCharacter);

    /// <summary>
    /// Letters, digits and the three separators that appear in GUIDs, W3C trace ids and
    /// conventional prefixed tokens. Control characters, whitespace and anything non-ASCII
    /// are excluded so the value cannot reshape a log line or a header.
    /// </summary>
    private static bool IsAllowedCharacter(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}

public static class CorrelationIdExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationIdMiddleware>();

    public static string GetCorrelationId(this HttpContext context)
        => context.Items[CorrelationIdMiddleware.HeaderName] as string ?? context.TraceIdentifier;
}
