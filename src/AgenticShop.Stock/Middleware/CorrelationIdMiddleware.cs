namespace AgenticShop.Stock.Middleware;

/// <summary>
/// Accepts an inbound X-Correlation-Id or mints one, then echoes it on the response.
/// Adopting an inbound value is what lets a single request be traced across
/// Ordering, Catalog and Stock once the orchestration lands.
/// </summary>
/// <remarks>
/// A deliberate copy of Catalog's middleware, not a shared type — see <c>docs/DECISIONS.md</c>.
/// Propagation is inbound only: Stock makes no outbound calls, so there is nothing to forward
/// to. Ordering will need a DelegatingHandler for the outbound direction.
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

        await next(context);
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
