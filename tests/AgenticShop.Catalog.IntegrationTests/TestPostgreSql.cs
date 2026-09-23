namespace AgenticShop.Catalog.IntegrationTests;

/// <summary>
/// The single declaration of which PostgreSQL server the integration tests run against.
/// <c>ComposeConfigurationTests</c> asserts that docker-compose.yml names the same tag, so
/// local development and the test suite cannot drift apart silently — otherwise the suite
/// validates a different server version than developers actually run against.
/// </summary>
internal static class TestPostgreSql
{
    public const string Image = "postgres:17-alpine";
}
