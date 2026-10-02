namespace AgenticShop.Shared.Errors;

/// <summary>
/// What an exception maps to on the wire: a status, an RFC 9457 title, an optional detail, and
/// optional extra ProblemDetails extensions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Detail"/> is null for every domain 400 and every 500, because invariant three says
/// no exception message reaches the client — those messages carry internal parameter names and are
/// formatted with the server's culture. A 409 is the exception to the rule, and only because its
/// detail is composed from the exception's *typed properties* rather than from <c>Message</c>.
/// </para>
/// <para>
/// <see cref="Extensions"/> exists so a service can put an identifier on a failure without forking
/// the handler. Ordering uses it for <c>orderId</c>: a failed placement still writes a row, and a
/// 409 or 502 that names no order leaves the client unable to tell "nothing was created" from
/// "something was created and is broken" — the ambiguity that causes blind retries against a
/// non-idempotent endpoint. <c>correlationId</c> is always added by the handler itself and must not
/// be supplied here.
/// </para>
/// </remarks>
public sealed record ExceptionClassification(
    int StatusCode,
    string Title,
    string? Detail,
    IReadOnlyDictionary<string, object?>? Extensions = null)
{
    /// <summary>Builds a classification carrying one extra extension, for the identifier case.</summary>
    public static ExceptionClassification WithExtension(
        int statusCode,
        string title,
        string? detail,
        string extensionName,
        object? extensionValue)
        => new(statusCode, title, detail,
            new Dictionary<string, object?>(StringComparer.Ordinal) { [extensionName] = extensionValue });
}
