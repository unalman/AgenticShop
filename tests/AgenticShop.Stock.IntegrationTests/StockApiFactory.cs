using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
internal sealed class StockApiFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    string brokerUrl = StockApiFactory.BrokerUrl) : WebApplicationFactory<Program>
{
    /// <summary>The environment the suite requires; see remarks.</summary>
    internal const string TestEnvironmentName = "Development";

    /// <summary>
    /// Unresolvable, and inert in the default fixture: <c>Program.cs</c> registers no messaging
    /// service yet, so nothing reads this key. It is supplied anyway because the messaging fixture
    /// uses this same factory, and because a value that is present-but-unreachable fails loudly
    /// rather than quietly reaching a broker on the developer's machine.
    /// </summary>
    internal const string BrokerUrl = "amqp://guest:guest@rabbitmq.invalid:5672/";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestEnvironmentName);

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Stock"] = connectionString,
                ["RabbitMq:Url"] = brokerUrl
            }));

        // ConfigureTestServices, not ConfigureServices: this callback runs after Program.cs has
        // registered everything, which is what lets the messaging fixture add the two hosted
        // services that Program.cs deliberately does not register yet.
        builder.ConfigureTestServices(services => configureServices?.Invoke(services));
    }
}
