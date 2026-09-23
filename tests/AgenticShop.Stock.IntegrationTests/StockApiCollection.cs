namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Sharing one fixture means one container and one migrated database for the whole collection.
/// xUnit also runs tests inside a collection sequentially, which is what makes
/// <see cref="StockApiFixture.ResetDatabaseAsync"/> safe between tests.
/// </summary>
[CollectionDefinition("Stock API")]
public sealed class StockApiCollection : ICollectionFixture<StockApiFixture>
{
}
