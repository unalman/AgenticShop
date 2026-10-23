namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Its own collection, so the broker-backed tests run sequentially among themselves and never in
/// parallel with the API collection — two hosts publishing to one queue would make "the message this
/// test placed" ambiguous.
/// </summary>
[CollectionDefinition("Ordering Messaging")]
public sealed class OrderingMessagingCollection : ICollectionFixture<OrderingMessagingFixture>
{
}
