namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// The single declaration of which RabbitMQ server Stock's messaging tests run against.
/// <c>ComposeConfigurationTests</c> asserts that docker-compose.yml names the same tag, so local
/// development and the test suite cannot drift apart silently.
/// </summary>
/// <remarks>
/// A copy of Ordering's equivalent rather than a shared constant, for the same reason the rest of the
/// harness is copied: the integration assemblies must not reference each other, and the compose
/// assertion in each is what keeps them equal. Catalog has no copy and must not gain one — it has no
/// broker container, so a guard there would assert nothing and pass vacuously.
/// </remarks>
internal static class TestRabbitMq
{
    public const string Image = "rabbitmq:4-alpine";
}
