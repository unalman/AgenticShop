using FluentAssertions;
using System.Text.RegularExpressions;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Guards the parts of docker-compose.yml that the code and tests depend on but cannot express as a
/// compile-time contract.
/// </summary>
/// <remarks>
/// A copy of Catalog's and Stock's equivalents rather than a shared test: the three integration
/// assemblies must not reference each other, and each needs its own check because each declares its
/// own <see cref="TestPostgreSql.Image"/>. This is the fourth declaration of the tag and the fourth
/// guard on it, which is the accepted cost of not sharing them — see <c>docs/KNOWN-ISSUES.md</c>.
/// </remarks>
public class ComposeConfigurationTests
{
    private static readonly Lazy<string> Compose = new(ReadComposeFile);

    /// <summary>
    /// A line that assigns a secret, in any of the shapes this repository uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be a plain <c>line.Contains("PASSWORD")</c>, which silently missed RabbitMQ's
    /// canonical <c>RABBITMQ_DEFAULT_PASS</c>: "PASS" is not a substring of "PASSWORD", so a literal
    /// broker password written into compose would have passed all three services' guards. Matching a
    /// secret-ish name followed by an assignment operator covers <c>POSTGRES_PASSWORD:</c>,
    /// <c>RABBITMQ_DEFAULT_PASS:</c> and the inline <c>Password=</c> inside a connection string alike.
    /// </para>
    /// <para>
    /// Case-insensitive because connection strings write <c>Password=</c>, not <c>PASSWORD=</c>. The
    /// old ordinal filter caught those lines only by accident, through the upper-case variable name
    /// inside the substitution they happened to contain.
    /// </para>
    /// </remarks>
    private static readonly Regex CredentialAssignment = new(
        @"(password|passwd|pass|secret|token|credential)\s*[:=]",
        RegexOptions.IgnoreCase);

    [Fact]
    public void TheComposeDatabaseImageMatchesTheOneTheTestsRunAgainst()
    {
        var images = DeclaredImages();

        // Compose declares a broker image as well now, so this selects the PostgreSQL one rather
        // than asserting there is only one. ContainSingle with a predicate still fails on an empty
        // sequence, so the guard cannot pass vacuously if the database image disappears.
        images.Should().ContainSingle(
            image => image.StartsWith("postgres:", StringComparison.Ordinal),
            "docker-compose.yml declares exactly one PostgreSQL image");

        images.Single(image => image.StartsWith("postgres:", StringComparison.Ordinal))
            .Should().Be(TestPostgreSql.Image);
    }

    /// <summary>
    /// The broker's tag, guarded the same way the database's is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test exists in Ordering's and Stock's copies and not in Catalog's, and the asymmetry tracks
    /// something real rather than an omission: those two services have a broker container in their
    /// harness and Catalog does not, so it has no <c>TestRabbitMq</c> constant to drift from. Copying
    /// the test there would assert nothing and pass vacuously — the failure mode <c>AGENTS.md</c> §10
    /// warns about for guards that match nothing.
    /// </para>
    /// <para>
    /// It was added because the alternative had already happened: <see cref="TestRabbitMq"/>'s
    /// remarks claimed this guard existed before it did. A documented guard that is not enforced is
    /// worse than no documentation, because it stops anyone looking.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheComposeBrokerImageMatchesTheOneTheTestsRunAgainst()
    {
        var images = DeclaredImages();

        images.Should().ContainSingle(
            image => image.StartsWith("rabbitmq:", StringComparison.Ordinal),
            "docker-compose.yml declares exactly one RabbitMQ image");

        images.Single(image => image.StartsWith("rabbitmq:", StringComparison.Ordinal))
            .Should().Be(TestRabbitMq.Image,
                "a broker version drift between compose and the messaging tests shows up as a client " +
                "incompatibility rather than as a failing assertion, which is quieter than the " +
                "database equivalent and therefore worth the same two lines");
    }

    [Fact]
    public void TheDatabasePortIsPublishedOnTheLoopbackInterfaceOnly()
    {
        // Publishing on every interface would expose the bootstrap superuser to every other host on
        // the local network.
        Compose.Value.Should().Contain("127.0.0.1:${POSTGRES_PORT",
            "the port mapping must pin the host address to loopback");

        // Checked against configuration lines only: prose in this repository explains the risk by
        // naming the wildcard address it is warning about.
        Lines().Should().NotContain(
            line => line.Contains("0.0.0.0", StringComparison.Ordinal),
            "no binding may publish on the wildcard address");
    }

    [Fact]
    public void EveryPublishedPortIsPinnedToTheLoopbackInterface()
    {
        // The test above rejects the literal 0.0.0.0, but that is not the only way to publish on
        // every interface: "5083:8080" binds all of them and contains no such literal. Compose now
        // publishes the service ports as well as PostgreSQL's, and nothing in this repository
        // authenticates a request, so the gap is worth closing rather than leaving to review.
        var mappings = PublishedPorts();

        mappings.Should().NotBeEmpty(
            "compose publishes PostgreSQL and the broker, plus the services behind the full profile");
        mappings.Should().OnlyContain(
            mapping => mapping.StartsWith("127.0.0.1:", StringComparison.Ordinal),
            "a mapping with no explicit host address publishes on every interface");

        // Non-vacuity for the one block this guard had not previously had to find. PublishedPorts
        // locates mappings by indentation, so a block written in flow form — ports: ["..."] — would
        // be invisible to it and OnlyContain would pass having inspected nothing. Matched on the
        // container-side port rather than on a substring of the whole mapping, because the host side
        // is a substitution (${RABBITMQ_PORT:-5672}) and contains no literal ":5672:".
        mappings.Should().Contain(
            mapping => mapping.EndsWith(":5672", StringComparison.Ordinal),
            "the broker's AMQP port must be among the mappings this guard actually inspected");
    }

    [Fact]
    public void EveryCredentialComesFromTheEnvironment()
    {
        var credentialLines = Lines().Where(IsCredentialLine).ToList();

        credentialLines.Should().NotBeEmpty("the compose file should be configuring credentials");
        credentialLines.Should().OnlyContain(
            line => line.Contains("${", StringComparison.Ordinal),
            "every credential must be substituted from the environment, never written literally");

        // Non-vacuity: the guard above would still pass if compose stopped configuring the broker
        // at all, and the image would then quietly fall back to guest/guest.
        credentialLines.Should().Contain(
            line => line.Contains("RABBITMQ_DEFAULT_PASS", StringComparison.Ordinal),
            "the broker password is a credential like any other and must come from the environment");
    }

    /// <summary>
    /// The regression test for the hole <see cref="CredentialAssignment"/>'s remarks describe: a
    /// literal broker password, which the old <c>Contains("PASSWORD")</c> filter never looked at.
    /// </summary>
    [Theory]
    [InlineData("RABBITMQ_DEFAULT_PASS: local_only_rabbit")]
    [InlineData("POSTGRES_PASSWORD: local_only_bootstrap")]
    [InlineData("ConnectionString: \"Host=db;Password=local_only_placeholder\"")]
    [InlineData("SOME_FUTURE_SECRET: hunter2")]
    public void ALiteralCredentialIsClassifiedWhateverTheVariableIsCalled(string line)
    {
        IsCredentialLine(line).Should().BeTrue(
            "a literal credential the classifier does not recognise can never be rejected");

        line.Should().NotContain("${",
            "the fixture is a literal value, which is what the environment guard exists to reject");
    }

    private static bool IsCredentialLine(string line) => CredentialAssignment.IsMatch(line);

    [Fact]
    public void TheOrderingRolePasswordIsSuppliedToTheBootstrapScript()
    {
        // init-dbs.sh creates ordering_svc from this variable. If it stops being passed, the role is
        // created with an empty password or the script fails — either way Ordering cannot connect,
        // and the failure would surface as a confusing startup error.
        Compose.Value.Should().Contain("ORDERING_DB_PASSWORD: ${ORDERING_DB_PASSWORD:");
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

    /// <summary>
    /// Every mapping under a <c>ports:</c> key, located by indentation rather than with a YAML
    /// parser — the same trade-off the rest of this class makes, and recorded in
    /// <c>docs/DECISIONS.md</c> under "Structural guards as tests".
    /// </summary>
    /// <remarks>
    /// Indentation is what distinguishes a port mapping from the similarly shaped entries under
    /// <c>volumes:</c>, which also start with <c>- </c> and also contain colons. A block stays open
    /// while its entries are indented further than the key that opened it, and the first line at or
    /// beyond that key's indent closes it.
    /// </remarks>
    private static List<string> PublishedPorts()
    {
        var mappings = new List<string>();
        var blockIndent = -1;

        foreach (var line in Compose.Value.Replace("\r", string.Empty).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - trimmed.Length;

            if (blockIndent >= 0)
            {
                if (indent > blockIndent && trimmed.StartsWith("- "))
                {
                    mappings.Add(trimmed["- ".Length..].Trim().Trim('"', '\''));
                    continue;
                }

                blockIndent = -1;
            }

            if (trimmed == "ports:")
            {
                blockIndent = indent;
            }
        }

        return mappings;
    }

    private static List<string> DeclaredImages()
        => Lines()
            .Where(line => line.StartsWith("image:", StringComparison.Ordinal))
            .Select(line => line["image:".Length..].Trim().Trim('"', '\''))
            .ToList();

    /// <summary>
    /// Walks up from the test output directory to the repository root. Confined to this one class so
    /// that a layout change breaks a single guard test with a clear message rather than the whole
    /// suite.
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
