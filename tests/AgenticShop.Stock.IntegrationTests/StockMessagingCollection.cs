namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Its own collection, so the broker-backed tests run sequentially among themselves and never in
/// parallel with the API collection — a background consumer mutating <c>stock_items</c> while another
/// test asserts on counters is not a test, it is a race.
/// </summary>
[CollectionDefinition("Stock Messaging")]
public sealed class StockMessagingCollection : ICollectionFixture<StockMessagingFixture>
{
}
