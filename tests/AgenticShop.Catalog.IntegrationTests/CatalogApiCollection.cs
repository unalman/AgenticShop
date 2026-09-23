namespace AgenticShop.Catalog.IntegrationTests;

/// <summary>
/// Sharing one fixture means one container and one migrated database for the whole
/// collection. xUnit also runs tests inside a collection sequentially, which is what
/// makes <see cref="CatalogApiFixture.ResetDatabaseAsync"/> safe between tests.
/// </summary>
[CollectionDefinition("Catalog API")]
public sealed class CatalogApiCollection : ICollectionFixture<CatalogApiFixture>
{
}
