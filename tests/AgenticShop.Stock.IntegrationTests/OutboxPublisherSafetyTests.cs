using System.Net;
using AgenticShop.Stock.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// The property that decides whether adding a publisher is safe at all: a broker that is down must cost
/// nothing but delayed events.
/// </summary>
/// <remarks>
/// <para>
/// An unhandled exception escaping <c>BackgroundService.ExecuteAsync</c> stops the host by default. So
/// the failure mode this guards is not "events are late" but "a broker outage takes down an API that
/// can still serve every read it serves and still write outbox rows" — the exact conflation the two
/// health endpoints exist to prevent, reintroduced through a background loop.
/// </para>
/// <para>
/// The same two properties are asserted against Ordering's copy of the publisher in its own suite,
/// because the assembly boundary forbids comparing the two implementations directly. That is what
/// "kept honest by a guard" means here: not a test that reads both files, but two tests that will each
/// fail if their own copy regresses.
/// </para>
/// </remarks>
[Collection("Stock Messaging")]
public sealed class OutboxPublisherSafetyTests(StockMessagingFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// Long enough for the publisher to have attempted a connection and failed at least once. The
    /// retry delay is five seconds, so three seconds catches the first attempt without waiting for a
    /// second.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AnUnreachableBrokerDoesNotStopTheHost()
    {
        // A second host on the same database, deliberately pointed at a broker that cannot resolve.
        // The collection's own host keeps a working broker so the other tests are unaffected; this one
        // needs the opposite condition, which is why it builds its own.
        await using var factory = new StockApiFactory(
            fixture.ConnectionString,
            services => services.AddHostedService<OutboxPublisher>(),
            brokerUrl: "amqp://guest:guest@rabbitmq.invalid:5672/");

        using var client = factory.CreateClient();

        // Non-vacuity, and the assertion that makes the rest of this test mean something. "The host
        // still answers" passes just as happily if the publisher was never registered, because then
        // nothing ever threw. Resolving the hosted service proves the thing under test is running.
        factory.Services
            .GetServices<IHostedService>()
            .Should()
            .Contain(service => service is OutboxPublisher,
                "the property under test is what the publisher does when it cannot connect, so it has " +
                "to be in the host for this test to exercise anything");

        await Task.Delay(SettleTime);

        var health = await client.GetAsync("/health");

        health.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a broker outage must not make Stock report itself down; /health checks only the database " +
            "this service owns, and the publisher's failure is contained inside its own loop");

        // A read that reaches the database, so "the host is up" is not satisfied by a middleware
        // short-circuit that never touches the parts a stopped host would have broken.
        var missing = await client.GetAsync($"/api/v1/stock/{Guid.NewGuid()}");

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
