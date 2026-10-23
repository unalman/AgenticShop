namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// The single declaration of which RabbitMQ server Ordering's messaging tests run against.
/// <c>ComposeConfigurationTests</c> asserts that docker-compose.yml names the same tag, so local
/// development and the test suite cannot drift apart silently.
/// </summary>
/// <remarks>
/// The same arrangement as <see cref="TestPostgreSql"/>, and the same reason for it: this
/// repository has already watched a container image tag drift between compose and a test fixture
/// with nothing noticing, which is what turned the constant into a guarded one. A broker version
/// drift is quieter than a database one — it usually shows up as a client library incompatibility
/// rather than a failing assertion — so it is worth the same two lines.
/// </remarks>
internal static class TestRabbitMq
{
    public const string Image = "rabbitmq:4-alpine";
}
