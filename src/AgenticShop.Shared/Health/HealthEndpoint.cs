using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace AgenticShop.Shared.Health;

/// <summary>
/// The two health endpoints, their response shape, and the rule about what may appear in it.
/// </summary>
/// <remarks>
/// <para>
/// The framework's default writer answers with the overall status as plain text, which says
/// "Unhealthy" and nothing about which dependency caused it — the whole point of naming the checks.
/// This one answers with JSON: entry names, statuses and durations.
/// </para>
/// <para>
/// <b><see cref="HealthReportEntry.Exception"/> is never written, in any environment.</b> That is the
/// load-bearing rule, and it is not conditional because the exception object is what would carry a
/// connection string. Verified rather than assumed: with the database stopped, the framework logged
/// the failure at Error internally while this response body contained a status, a duration and nothing
/// else.
/// </para>
/// <para>
/// <b>Descriptions are gated on Development as defence in depth, not because one leaks today.</b>
/// Also verified: <c>AddDbContextCheck</c> leaves <c>Description</c> null on failure and puts the
/// exception in <c>Exception</c>, so with the database down the body carries no description at all —
/// an earlier draft of this comment claimed it exposed host, port and database name, and that was
/// wrong. The gate stays because the framework's own <c>UIResponseWriter</c> emits descriptions
/// unconditionally and third-party checks commonly derive them from exception messages, so the first
/// check added for a broker or a cache could leak through a writer that did not think about it. It
/// also keeps a downstream probe's prose ("Catalog did not respond") where it is useful — in front of
/// a developer — and out of a production response that only needs the status.
/// </para>
/// </remarks>
public static class HealthEndpoint
{
    /// <summary>
    /// Liveness: "is this process able to serve requests". Depends only on what the service owns —
    /// its own database — and never on another service, so one service's outage cannot mark its
    /// dependents down as well.
    /// </summary>
    public const string Path = "/health";

    /// <summary>
    /// Readiness: "can this service do its job right now". Only Ordering maps it, because only
    /// Ordering has a downstream to be unready for; in a leaf service it would be a second endpoint
    /// returning exactly what <see cref="Path"/> already returns.
    /// </summary>
    public const string ReadyPath = "/health/ready";

    /// <summary>
    /// Marks a check as readiness-only, which is what keeps it out of <see cref="Path"/>. A check that
    /// depends on another service must carry this tag.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// The entry name every service gives its database check. <c>AddDbContextCheck</c> would
    /// otherwise default to the context type name, which differs per service — and a consumer reading
    /// three responses should not have to know that Catalog's database is called
    /// <c>CatalogDbContext</c>.
    /// </summary>
    public const string DatabaseCheckName = "database";

    /// <summary>
    /// Options for <see cref="Path"/>: every check that is not tagged <see cref="ReadyTag"/>.
    /// </summary>
    public static HealthCheckOptions Liveness(bool includeDescriptions)
        => Options(includeDescriptions, registration => !registration.Tags.Contains(ReadyTag));

    /// <summary>
    /// Options for <see cref="ReadyPath"/>: every check, since being unready for want of a database is
    /// as true as being unready for want of a downstream.
    /// </summary>
    public static HealthCheckOptions Ready(bool includeDescriptions)
        => Options(includeDescriptions, registration => true);

    private static HealthCheckOptions Options(
        bool includeDescriptions,
        Func<HealthCheckRegistration, bool> predicate) => new()
    {
        Predicate = predicate,
        ResponseWriter = (context, report) => WriteAsync(context, report, includeDescriptions)
    };

    private static async Task WriteAsync(
        HttpContext context,
        HealthReport report,
        bool includeDescriptions)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        // await using, not a try/finally around a returned Task: Utf8JsonWriter buffers, and disposing
        // it before the flush completes would race the write to the response body.
        await using var writer = new Utf8JsonWriter(
            context.Response.Body,
            new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("status", report.Status.ToString());
        writer.WriteString("totalDuration", report.TotalDuration.ToString("c"));

        writer.WriteStartObject("entries");

        foreach (var (name, entry) in report.Entries)
        {
            writer.WriteStartObject(name);
            writer.WriteString("status", entry.Status.ToString());
            writer.WriteString("duration", entry.Duration.ToString("c"));

            // Both guards matter: an empty description adds noise, and outside Development a
            // description is caller-invisible by design.
            if (includeDescriptions && entry.Description is { Length: > 0 })
            {
                writer.WriteString("description", entry.Description);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();

        await writer.FlushAsync();
    }
}
