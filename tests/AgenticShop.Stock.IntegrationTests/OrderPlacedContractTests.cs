using System.Text.Json;
using AgenticShop.Stock.Messaging;
using FluentAssertions;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// That Stock's copy of the contract reads what Ordering's copy actually writes.
/// </summary>
/// <remarks>
/// <para>
/// The two records live in assemblies that must not reference each other, so nothing at compile time
/// connects them. What connects them is a JSON payload, and a copy can drift in a way no type checker
/// sees: a renamed property, a different casing policy, a field the producer added. Every one of those
/// deserialises successfully into defaults, so the failure is a consumer that silently reads an empty
/// order rather than an exception.
/// </para>
/// <para>
/// The fixture below is not a hand-written mirror of what I believe Ordering sends. It is a payload
/// captured from a real placement driven through the compose stack, read out of
/// <c>outbox_messages.payload_json</c>. If Ordering's contract changes, this test fails against the
/// bytes that were actually on the wire.
/// </para>
/// </remarks>
public sealed class OrderPlacedContractTests
{
    /// <summary>
    /// Captured 2026-10-23 from <c>agenticshop_ordering.outbox_messages</c> after a real placement
    /// through <c>docker compose --profile full</c>. Verbatim, including the seven-digit fraction on
    /// the timestamp, because that precision is part of what has to round-trip.
    /// </summary>
    private const string CapturedFromCompose =
        """
        {"messageId":"ff3c335a-57b5-4f5c-8f7f-837267f8ec43","orderId":"ef696416-5bf3-450d-a17a-4146423f43ab","orderNumber":"ORD-20261023-ef696416","currency":"USD","occurredAtUtc":"2026-10-23T04:16:47.3073842+00:00","lines":[{"productId":"6963107d-f9a8-4d2b-9a05-bfd44a3673dc","quantity":2}]}
        """;

    [Fact]
    public void TheContractReadsThePayloadOrderingActuallyPublished()
    {
        var parsed = JsonSerializer.Deserialize<OrderPlaced>(CapturedFromCompose, StockMessageJson.Options);

        parsed.Should().NotBeNull("the captured payload is the shape Stock must be able to read");

        parsed!.MessageId.Should().Be(Guid.Parse("ff3c335a-57b5-4f5c-8f7f-837267f8ec43"));
        parsed.OrderId.Should().Be(Guid.Parse("ef696416-5bf3-450d-a17a-4146423f43ab"));
        parsed.OrderNumber.Should().Be("ORD-20261023-ef696416");
        parsed.Currency.Should().Be("USD");
        parsed.OccurredAtUtc.Should().Be(DateTimeOffset.Parse("2026-10-23T04:16:47.3073842+00:00"));

        // Every field asserted, not a sample of them. A property Stock forgot to declare would
        // deserialize as a default and nothing else here would notice.
        parsed.Lines.Should().ContainSingle();
        parsed.Lines[0].ProductId.Should().Be(Guid.Parse("6963107d-f9a8-4d2b-9a05-bfd44a3673dc"));
        parsed.Lines[0].Quantity.Should().Be(2);
    }

    /// <summary>
    /// Guards the guard. The test above would still pass if <see cref="StockMessageJson.Options"/>
    /// drifted to a policy that read nothing at all — every assertion would be against a value the
    /// fixture happened to spell the same way. This one fails if the contract stops being
    /// case-sensitive in the direction that matters.
    /// </summary>
    [Fact]
    public void TheContractDoesNotReadASnakeCasedPayload()
    {
        const string snakeCased =
            """
            {"message_id":"ff3c335a-57b5-4f5c-8f7f-837267f8ec43","order_id":"ef696416-5bf3-450d-a17a-4146423f43ab","lines":[{"product_id":"6963107d-f9a8-4d2b-9a05-bfd44a3673dc","quantity":2}]}
            """;

        var parsed = JsonSerializer.Deserialize<OrderPlaced>(snakeCased, StockMessageJson.Options);

        parsed.Should().NotBeNull("unknown fields are ignored rather than rejected, which is its own risk");

        parsed!.OrderId.Should().Be(Guid.Empty,
            "the camelCase contract must not read snake_case fields; if this ever stops being empty " +
            "the two services have drifted and the consumer is reading defaults");

        // `lines` and `quantity` happen to be spelled the same in both casings, so they still bind —
        // and that is what makes the drift silent rather than loud. One field reads correctly, the one
        // beside it does not, and nothing throws. A consumer in this state holds stock for product
        // 00000000-0000-0000-0000-000000000000.
        parsed.Lines.Should().ContainSingle();
        parsed.Lines[0].Quantity.Should().Be(2);
        parsed.Lines[0].ProductId.Should().Be(Guid.Empty,
            "product_id is not productId, so the one field that says what to hold is the one lost");
    }

    /// <summary>
    /// The same guarantee in the outbound direction: what Stock publishes must be readable by a
    /// consumer using web-default settings, which is what Ordering will use.
    /// </summary>
    [Fact]
    public void TheOutcomesSerialiseAsCamelCaseWithNamedEnums()
    {
        var reserved = JsonSerializer.Serialize(
            new StockReserved(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                [new StockReservedLine(Guid.NewGuid(), 3)]),
            StockMessageJson.Options);

        reserved.Should().Contain("\"orderId\"").And.Contain("\"lines\"").And.Contain("\"productId\"");
        reserved.Should().NotContain("\"OrderId\"", "a PascalCase payload would be a second wire format");

        var rejected = JsonSerializer.Serialize(
            new StockReservationRejected(
                Guid.NewGuid(),
                Guid.NewGuid(),
                RejectionReason.InsufficientStock,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow),
            StockMessageJson.Options);

        rejected.Should().Contain("\"reason\":\"InsufficientStock\"",
            "an enum serialised as its ordinal would silently change meaning if a member were inserted");
    }
}
