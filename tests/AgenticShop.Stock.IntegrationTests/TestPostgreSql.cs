namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// The single declaration of which PostgreSQL server Stock's integration tests run against.
/// <c>ComposeConfigurationTests</c> asserts that docker-compose.yml names the same tag, so local
/// development and the test suite cannot drift apart silently.
/// </summary>
/// <remarks>
/// Deliberately a separate constant from Catalog's rather than a shared one: the two test
/// assemblies must not reference each other, and the compose assertion in each is what keeps
/// them equal. See <c>docs/KNOWN-ISSUES.md</c>.
/// </remarks>
internal static class TestPostgreSql
{
    public const string Image = "postgres:17-alpine";
}
