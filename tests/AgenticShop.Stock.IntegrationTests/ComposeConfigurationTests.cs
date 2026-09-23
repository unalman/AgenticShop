using FluentAssertions;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Guards the parts of docker-compose.yml that the code and tests depend on but cannot express
/// as a compile-time contract.
/// </summary>
/// <remarks>
/// A copy of Catalog's equivalent rather than a shared test: the two integration assemblies must
/// not reference each other, and each needs its own check because each declares its own
/// <see cref="TestPostgreSql.Image"/>. Without this, Stock's fixture could drift onto a different
/// PostgreSQL version than the one developers run.
/// </remarks>
public class ComposeConfigurationTests
{
    private static readonly Lazy<string> Compose = new(ReadComposeFile);

    [Fact]
    public void TheComposeDatabaseImageMatchesTheOneTheTestsRunAgainst()
    {
        var images = DeclaredImages();

        images.Should().ContainSingle("docker-compose.yml declares exactly one service image");
        images.Single().Should().Be(TestPostgreSql.Image);
    }

    [Fact]
    public void TheDatabasePortIsPublishedOnTheLoopbackInterfaceOnly()
    {
        // Publishing on every interface would expose the bootstrap superuser to every other host
        // on the local network.
        Compose.Value.Should().Contain("127.0.0.1:${POSTGRES_PORT",
            "the port mapping must pin the host address to loopback");

        // Checked against configuration lines only: prose in this repository explains the risk
        // by naming the wildcard address it is warning about.
        Lines().Should().NotContain(
            line => line.Contains("0.0.0.0", StringComparison.Ordinal),
            "no binding may publish on the wildcard address");
    }

    [Fact]
    public void EveryCredentialComesFromTheEnvironment()
    {
        var credentialLines = Lines()
            .Where(line => line.Contains("PASSWORD", StringComparison.Ordinal))
            .ToList();

        credentialLines.Should().NotBeEmpty("the compose file should be configuring credentials");
        credentialLines.Should().OnlyContain(
            line => line.Contains("${", StringComparison.Ordinal),
            "every credential must be substituted from the environment, never written literally");
    }

    [Fact]
    public void TheStockRolePasswordIsSuppliedToTheBootstrapScript()
    {
        // init-dbs.sh creates stock_svc from this variable. If it stops being passed, the role
        // is created with an empty password or the script fails — either way Stock cannot
        // connect, and the failure would surface as a confusing startup error.
        Compose.Value.Should().Contain("STOCK_DB_PASSWORD: ${STOCK_DB_PASSWORD:");
    }

    [Fact]
    public void TheIsolationVerificationScriptIsMountedIntoTheContainer()
    {
        Compose.Value.Should().Contain("scripts/verify-db-isolation.sh",
            "the isolation verifier must be reachable inside the container so the documented command works");
    }

    private static IEnumerable<string> Lines()
        => Compose.Value
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'));

    private static List<string> DeclaredImages()
        => Lines()
            .Where(line => line.StartsWith("image:", StringComparison.Ordinal))
            .Select(line => line["image:".Length..].Trim().Trim('"', '\''))
            .ToList();

    /// <summary>
    /// Walks up from the test output directory to the repository root. Confined to this one class
    /// so that a layout change breaks a single guard test with a clear message rather than the
    /// whole suite.
    /// </summary>
    private static string ReadComposeFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull(
            "docker-compose.yml should be reachable by walking up from the test output directory");

        return File.ReadAllText(Path.Combine(directory!.FullName, "docker-compose.yml"));
    }
}
