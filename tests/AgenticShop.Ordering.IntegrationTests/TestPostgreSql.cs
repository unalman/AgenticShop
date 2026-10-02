namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// The single declaration of which PostgreSQL server Ordering's integration tests run against.
/// <c>ComposeConfigurationTests</c> asserts that docker-compose.yml names the same tag, so local
/// development and the test suite cannot drift apart silently.
/// </summary>
/// <remarks>
/// Deliberately a separate constant from Catalog's and Stock's rather than a shared one: the three
/// test assemblies must not reference each other, and the compose assertion in each is what keeps
/// them equal. This is the fourth declaration of the tag, and the fourth guard on it — see
/// <c>docs/KNOWN-ISSUES.md</c>.
/// </remarks>
internal static class TestPostgreSql
{
    public const string Image = "postgres:17-alpine";
}
