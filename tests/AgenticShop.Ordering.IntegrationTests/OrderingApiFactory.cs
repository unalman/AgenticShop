using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Boots the real Ordering API in-process, overriding the environment, the connection string and
/// the two downstream clients.
/// </summary>
/// <remarks>
/// <para>
/// The environment is declared rather than inherited from WebApplicationFactory's implicit
/// "Development" default, because the tests depend on that value: Program.cs runs
/// <c>MigrateAsync()</c> only when <c>IsDevelopment()</c>, and without it the schema would not
/// exist.
/// </para>
/// <para>
/// The downstream base addresses point at <c>.invalid</c>, a TLD RFC 2606 reserves so it can never
/// resolve. The real clients are replaced, so nothing should ever dial them; if a replacement ever
/// stops applying, a test fails on an unresolvable host instead of quietly writing to a Catalog or
/// Stock running on this machine. Program.cs validates both URLs at startup, so they have to be
/// present and absolute even though they are never used.
/// </para>
/// </remarks>
internal sealed class OrderingApiFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    string catalogBaseUrl = OrderingApiFactory.CatalogBaseUrl,
    string stockBaseUrl = OrderingApiFactory.StockBaseUrl) : WebApplicationFactory<Program>
{
    /// <summary>The environment the suite requires; see remarks.</summary>
    internal const string TestEnvironmentName = "Development";

    internal const string CatalogBaseUrl = "http://catalog.invalid";
    internal const string StockBaseUrl = "http://stock.invalid";

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
                ["Services:Stock:BaseUrl"] = stockBaseUrl
            }));

        // ConfigureTestServices, not ConfigureServices: this callback runs after Program.cs has
        // registered everything, so removing and re-adding actually replaces the typed clients
        // instead of leaving two competing registrations behind.
        builder.ConfigureTestServices(services => configureServices?.Invoke(services));
    }
}
