namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Sharing one fixture means one container, one migrated database and one pair of fakes for the
/// whole collection. xUnit also runs tests inside a collection sequentially, which is what makes
/// <see cref="OrderingApiFixture.ResetAsync"/> safe between tests — the fakes hold mutable scripted
/// state, so a parallel test would see another test's script.
/// </summary>
[CollectionDefinition("Ordering API")]
public sealed class OrderingApiCollection : ICollectionFixture<OrderingApiFixture>
{
}
