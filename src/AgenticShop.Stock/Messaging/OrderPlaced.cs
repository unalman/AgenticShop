namespace AgenticShop.Stock.Messaging;

/// <summary>
/// The event Stock consumes. A copy of Ordering's contract, not a reference to it.
/// </summary>
/// <remarks>
/// <para>
/// Duplicated because <c>Directory.Build.targets</c> forbids a reference between two services, and
/// because <c>LeafServiceBoundaryTests</c> asserts the referenced <c>AgenticShop.*</c> set is exactly
/// <c>["AgenticShop.Shared"]</c> — so a shared contracts assembly is not a convention breach here but
/// a hard test failure. <c>AGENTS.md</c> §10 makes the same call for the test harness: copy, and let a
/// guard keep the copies honest.
/// </para>
/// <para>
/// <b>The guard is the wire format, not the C# shape.</b> Two records with the same property names can
/// still disagree, because what crosses the broker is JSON and the settings that produced it. So
/// <c>OrderPlacedContractTests</c> deserialises a literal payload captured from a real placement
/// through compose and asserts every field landed. If this copy drifts — a renamed property, a
/// different casing policy, a field Ordering added — that test fails, and it fails against the bytes
/// on the wire rather than against a mirror of my own assumptions.
/// </para>
/// </remarks>
/// <param name="MessageId">The producer's outbox row id. Stable across redelivery, and the key the inbox deduplicates on.</param>
/// <param name="OrderId">Cross-service references are ids, never keys.</param>
/// <param name="OrderNumber">The human reference, carried so an operator can find the order from a message alone.</param>
/// <param name="Currency">Informational here. Stock holds no money at all, so nothing is priced from it.</param>
/// <param name="OccurredAtUtc">When the order was placed — not when the message was published.</param>
/// <param name="Lines">What to reserve. One entry per product; the producer has already combined duplicates.</param>
public sealed record OrderPlaced(
    Guid MessageId,
    Guid OrderId,
    string OrderNumber,
    string Currency,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<OrderPlacedLine> Lines);

/// <summary>One product to hold, and how many of it.</summary>
public sealed record OrderPlacedLine(Guid ProductId, int Quantity);
