using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Boots the real Ordering API in-process, overriding the environment, the connection string and
/// the downstream client.
/// </summary>
/// <remarks>
/// <para>
/// The environment is declared rather than inherited from WebApplicationFactory's implicit
/// "Development" default, because the tests depend on that value: Program.cs runs
/// <c>MigrateAsync()</c> only when <c>IsDevelopment()</c>, and without it the schema would not
/// exist.
/// </para>
/// <para>
/// The downstream base address points at <c>.invalid</c>, a TLD RFC 2606 reserves so it can never
/// resolve. The real client is replaced, so nothing should ever dial it; if a replacement ever
/// stops applying, a test fails on an unresolvable host instead of quietly writing to a Catalog
/// running on this machine. Program.cs validates the URL at startup, so it has to be present and
/// absolute even though it is never used.
/// </para>
/// </remarks>
internal sealed class OrderingApiFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    string catalogBaseUrl = OrderingApiFactory.CatalogBaseUrl,
    string brokerUrl = OrderingApiFactory.BrokerUrl) : WebApplicationFactory<Program>
{
    /// <summary>The environment the suite requires; see remarks.</summary>
    internal const string TestEnvironmentName = "Development";

    internal const string CatalogBaseUrl = "http://catalog.invalid";

    /// <summary>
    /// Unresolvable for the same reason the base address is, and it has to be present at all
    /// because Program.cs validates the broker URL at startup. Nothing in the suite should reach a
    /// real broker by accident; the one test that wants a real one — the outbox publisher — supplies
    /// its own and starts a container for it.
    /// </summary>
    internal const string BrokerUrl = "amqp://guest:guest@rabbitmq.invalid:5672/";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestEnvironmentName);

        // Both keys are always set, so an in-memory override beats appsettings.Development.json
        // rather than merely failing to replace it — which is what lets a test supply an empty or
        // relative value and prove the startup validation actually rejects it.
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ordering"] = connectionString,
                ["Services:Catalog:BaseUrl"] = catalogBaseUrl,
                ["RabbitMq:Url"] = brokerUrl
            }));

        // ConfigureTestServices, not ConfigureServices: this callback runs after Program.cs has
        // registered everything, so removing and re-adding actually replaces the typed clients
        // instead of leaving two competing registrations behind.
        builder.ConfigureTestServices(services => configureServices?.Invoke(services));
    }
}
