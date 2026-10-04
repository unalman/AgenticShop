using AgenticShop.Shared.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text;
using System.Text.Json;

namespace AgenticShop.Shared.UnitTests;

/// <summary>
/// The health response contract: what the two endpoints select, and what the body may contain.
/// </summary>
/// <remarks>
/// Specified here once rather than per service, because <see cref="HealthEndpoint"/> is shared. Each
/// service's integration suite asserts only that its own endpoints are wired.
/// </remarks>
public class HealthEndpointTests
{
    /// <summary>Stands in for a connection string that must never reach a response.</summary>
    private const string Secret = "Password=local_only_catalog";

    // --- What the body may contain ------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheExceptionIsNeverWrittenInAnyEnvironment(bool includeDescriptions)
    {
        // The load-bearing rule. A health endpoint is the one place an unauthenticated caller can
        // make a dependency fail on demand, and the exception object is what would carry a connection
        // string. Gating this on environment would leave the gate open wherever it matters.
        var body = await WriteAsync(
            Report(("database", Unhealthy(exception: new InvalidOperationException(Secret)))),
            includeDescriptions);

        body.Should().NotContain(Secret);
        body.Should().NotContain("InvalidOperationException");
        body.Should().Contain("\"status\": \"Unhealthy\"");
    }

    [Fact]
    public async Task ADescriptionIsWrittenInDevelopmentOnly()
    {
        var report = Report(("Catalog", Unhealthy(description: "Catalog did not respond")));

        (await WriteAsync(report, includeDescriptions: true))
            .Should().Contain("Catalog did not respond");

        (await WriteAsync(report, includeDescriptions: false))
            .Should().NotContain("Catalog did not respond",
                "a production consumer needs the status, not the prose");
    }

    [Fact]
    public async Task AnEntryIsNamedAndCarriesItsStatusAndDuration()
    {
        // Naming is the reason for a custom writer at all: the framework default answers "Unhealthy"
        // and does not say which dependency caused it.
        var body = await WriteAsync(
            Report(
                ("database", Healthy()),
                ("Stock", Unhealthy(description: "Stock reported 503"))),
            includeDescriptions: true);

        using var json = JsonDocument.Parse(body);

        json.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");

        var entries = json.RootElement.GetProperty("entries");

        entries.GetProperty("database").GetProperty("status").GetString().Should().Be("Healthy");
        entries.GetProperty("Stock").GetProperty("status").GetString().Should().Be("Unhealthy");

        entries.GetProperty("database").GetProperty("duration").GetString()
            .Should().NotBeNullOrEmpty("a slow dependency is the thing worth diagnosing");
    }

    [Fact]
    public async Task AnEmptyDescriptionIsOmittedRatherThanWrittenAsNull()
    {
        // AddDbContextCheck leaves Description null on failure, so this is the common case for the one
        // check every service has — writing an empty member for it would be noise in every response.
        var body = await WriteAsync(Report(("database", Unhealthy())), includeDescriptions: true);

        using var json = JsonDocument.Parse(body);

        json.RootElement.GetProperty("entries").GetProperty("database")
            .EnumerateObject()
            .Select(property => property.Name)
            .Should().BeEquivalentTo(["status", "duration"]);
    }

    // --- What each endpoint selects -----------------------------------------------------

    [Fact]
    public void LivenessExcludesReadinessOnlyChecks()
    {
        // The property that stops one outage becoming two: a check on another service must never run
        // on /health, or a downstream failure marks this service down too.
        var selects = PredicateOf(HealthEndpoint.Liveness(includeDescriptions: false));

        selects(Registration("database")).Should().BeTrue();
        selects(Registration("Catalog", HealthEndpoint.ReadyTag)).Should().BeFalse();
    }

    [Fact]
    public void ReadinessIncludesEverything()
    {
        // Being unready for want of a database is as true as being unready for want of a downstream.
        var selects = PredicateOf(HealthEndpoint.Ready(includeDescriptions: false));

        selects(Registration("database")).Should().BeTrue();
        selects(Registration("Catalog", HealthEndpoint.ReadyTag)).Should().BeTrue();
    }

    [Fact]
    public void TheContractConstantsAreWhatTheEndpointsAndTestsAssume()
    {
        // Pinned because three services map these paths, Ordering's probe builds a URI from Path, and
        // every health assertion in the integration suites looks up DatabaseCheckName. Routing matches
        // exact templates rather than prefixes, so /health and /health/ready cannot shadow each other.
        HealthEndpoint.Path.Should().Be("/health");
        HealthEndpoint.ReadyPath.Should().Be("/health/ready");
        HealthEndpoint.DatabaseCheckName.Should().Be("database");
    }

    // --- Helpers ------------------------------------------------------------------------

    private static Func<HealthCheckRegistration, bool> PredicateOf(HealthCheckOptions options)
    {
        options.Predicate.Should().NotBeNull("both factories set one");

        return options.Predicate!;
    }

    private static async Task<string> WriteAsync(HealthReport report, bool includeDescriptions)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        var options = HealthEndpoint.Ready(includeDescriptions);

        await options.ResponseWriter(context, report);

        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries) => new(
        entries.ToDictionary(entry => entry.Name, entry => entry.Entry),
        TimeSpan.FromMilliseconds(42));

    private static HealthReportEntry Healthy() => new(
        HealthStatus.Healthy,
        description: null,
        duration: TimeSpan.FromMilliseconds(3),
        exception: null,
        data: null);

    private static HealthReportEntry Unhealthy(string? description = null, Exception? exception = null) => new(
        HealthStatus.Unhealthy,
        description,
        TimeSpan.FromMilliseconds(78),
        exception,
        data: null);

    private static HealthCheckRegistration Registration(string name, params string[] tags) => new(
        name,
        new NoopCheck(),
        failureStatus: null,
        tags);

    private sealed class NoopCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
