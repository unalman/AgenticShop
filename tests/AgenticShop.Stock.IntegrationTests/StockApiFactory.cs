using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Boots the real Stock API in-process, overriding only the environment and the connection
/// string so it points at the throwaway Testcontainers database.
/// </summary>
/// <remarks>
/// The environment is declared rather than inherited from WebApplicationFactory's implicit
/// "Development" default, because the tests depend on that value: Program.cs runs
/// <c>MigrateAsync()</c> only when <c>IsDevelopment()</c>, and without it the schema would not
/// exist. TestHostIsolationTests fails loudly if the connection-string override stops applying.
/// </remarks>
internal sealed class StockApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>The environment the suite requires; see remarks.</summary>
    internal const string TestEnvironmentName = "Development";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestEnvironmentName);

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Stock"] = connectionString
            }));
    }
}
