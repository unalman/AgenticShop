using AgenticShop.Stock.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Boots the real Stock API in-process, overriding only the environment and the connection
/// string so it points at the throwaway Testcontainers database.
/// </summary>
/// <remarks>
/// <para>
/// The environment is declared rather than inherited from WebApplicationFactory's implicit
/// "Development" default, because the tests depend on that value: Program.cs runs
/// <c>MigrateAsync()</c> only when <c>IsDevelopment()</c>, and without it the schema would not
/// exist. TestHostIsolationTests fails loudly if the connection-string override stops applying.
/// </para>
/// <para>
/// The SQL capture is the one addition over the harness Catalog and Ordering copy verbatim.
/// <c>SettleReadAtomicityTests</c> needs to count the statements a settle request issues, which is
/// only observable from inside the host's DbContext; see <see cref="SettleSqlCapture"/> for why a
/// parallel burst cannot substitute. Recorded as a divergence in
/// <c>src/AgenticShop.Stock/docs/ARCHITECTURE.md</c> §8 rather than carried back, because neither
/// other service has a two-row read whose atomicity is load-bearing.
/// </para>
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

    /// <summary>
    /// Records the host's SQL while a test holds a capture window open.
    /// </summary>
    /// <remarks>
    /// Attached through <see cref="IDbContextOptionsConfiguration{TContext}"/> rather than by
    /// registering an <c>IInterceptor</c> in the container. The DI route was tried first and the
    /// capture saw nothing; the reason was not established, and this is the mechanism that was
    /// verified to work. What made the dead end cheap to find is the non-vacuity assertion in
    /// <c>SettleReadAtomicityTests</c> — without it the guard would have been recording no SQL and
    /// asserting on an empty list.
    /// </remarks>
    internal SettleSqlCapture SqlCapture { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestEnvironmentName);

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Stock"] = connectionString,
                ["RabbitMq:Url"] = brokerUrl
            }));

        builder.ConfigureServices(services => services
            .AddSingleton<IDbContextOptionsConfiguration<StockDbContext>>(
                new SqlCaptureConfiguration(SqlCapture)));

        // ConfigureTestServices, not ConfigureServices: this callback runs after Program.cs has
        // registered everything, which is what lets the messaging fixture add the two hosted
        // services that Program.cs deliberately does not register yet.
        builder.ConfigureTestServices(services => configureServices?.Invoke(services));
    }

    /// <summary>Adds <see cref="SettleSqlCapture"/> to the options EF builds for the host.</summary>
    private sealed class SqlCaptureConfiguration(SettleSqlCapture capture)
        : IDbContextOptionsConfiguration<StockDbContext>
    {
        public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder builder)
            => builder.AddInterceptors(capture);
    }
}
