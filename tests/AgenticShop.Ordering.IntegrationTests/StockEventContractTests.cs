using System.Text.Json;
using AgenticShop.Ordering.Messaging;
using FluentAssertions;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// That Ordering's copy of Stock's outcome contracts reads what Stock's copy actually writes.
/// </summary>
/// <remarks>
/// <para>
/// The two sets of records live in assemblies that must not reference each other, so nothing at
/// compile time connects them. What connects them is JSON, and a copy can drift in a way no type
/// checker sees — a renamed property, a different casing policy, an enum that switched from name to
/// ordinal. Every one of those deserialises successfully into defaults, so the failure is a consumer
/// that silently reads an empty order rather than an exception.
/// </para>
/// <para>
/// <b>These payloads were not captured from a live run, and the reason matters.</b> Stock's consumer
/// is built but not registered, so nothing in the compose stack produces a real outcome event to
/// capture. Instead the field names below are the ones Stock's own suite asserts its serializer
/// produces: <c>TheOutcomesSerialiseAsCamelCaseWithNamedEnums</c> pins <c>orderId</c>, <c>lines</c>,
/// <c>productId</c> and <c>"reason":"InsufficientStock"</c>, and asserts no PascalCase form appears.
/// That makes this two independent assertions on one wire format rather than one shared fixture — the
/// same arrangement that keeps the duplicated PostgreSQL image tag honest. If either side drifts, one
/// of the two tests fails, and it fails naming the field.
/// </para>
/// </remarks>
public sealed class StockEventContractTests
{
    private const string ReservedPayload =
        """
        {"messageId":"3f2b8c14-9d47-4e6a-b0f5-1c8a7d2e4b90","orderId":"ef696416-5bf3-450d-a17a-4146423f43ab","occurredAtUtc":"2026-10-23T05:12:44.8123456+00:00","lines":[{"productId":"6963107d-f9a8-4d2b-9a05-bfd44a3673dc","quantity":2}]}
        """;

    private const string RejectedPayload =
        """
        {"messageId":"a91c4e07-2b68-4f31-9d05-77e2c1b84a63","orderId":"ef696416-5bf3-450d-a17a-4146423f43ab","reason":"InsufficientStock","failedProductId":"6963107d-f9a8-4d2b-9a05-bfd44a3673dc","occurredAtUtc":"2026-10-23T05:12:44.8123456+00:00"}
        """;

    [Fact]
    public void TheContractReadsAReservedOutcome()
    {
        var parsed = JsonSerializer.Deserialize<StockReserved>(ReservedPayload, OrderPlaced.JsonOptions);

        parsed.Should().NotBeNull();

        parsed!.MessageId.Should().Be(Guid.Parse("3f2b8c14-9d47-4e6a-b0f5-1c8a7d2e4b90"));
        parsed.OrderId.Should().Be(Guid.Parse("ef696416-5bf3-450d-a17a-4146423f43ab"));
        parsed.OccurredAtUtc.Should().Be(DateTimeOffset.Parse("2026-10-23T05:12:44.8123456+00:00"));

        // Every field asserted, not a sample. A property Ordering forgot to declare would read as a
        // default and nothing else here would notice.
        parsed.Lines.Should().ContainSingle();
        parsed.Lines[0].ProductId.Should().Be(Guid.Parse("6963107d-f9a8-4d2b-9a05-bfd44a3673dc"));
        parsed.Lines[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void TheContractReadsARejectedOutcomeWithANamedEnum()
    {
        var parsed = JsonSerializer.Deserialize<StockReservationRejected>(
            RejectedPayload, OrderPlaced.JsonOptions);

        parsed.Should().NotBeNull();

        parsed!.MessageId.Should().Be(Guid.Parse("a91c4e07-2b68-4f31-9d05-77e2c1b84a63"));
        parsed.OrderId.Should().Be(Guid.Parse("ef696416-5bf3-450d-a17a-4146423f43ab"));
        parsed.FailedProductId.Should().Be(Guid.Parse("6963107d-f9a8-4d2b-9a05-bfd44a3673dc"));

        // The reason is what the saga acts on, and it crosses the wire as a name. An ordinal would
        // have deserialised to UnknownProduct here — the zero value — which reads as a legitimate
        // answer rather than as a mismatch.
        parsed.Reason.Should().Be(RejectionReason.InsufficientStock);
    }

    /// <summary>
    /// Guards the guard. Both tests above would still pass if <see cref="OrderPlaced.JsonOptions"/>
    /// drifted to a policy that read nothing, because a default <see cref="Guid"/> is a perfectly
    /// valid value to compare against a fixture I wrote. This one fails if the contract stops being
    /// case-sensitive in the direction that matters.
    /// </summary>
    [Fact]
    public void TheContractDoesNotReadASnakeCasedOutcome()
    {
        const string snakeCased =
            """
            {"message_id":"3f2b8c14-9d47-4e6a-b0f5-1c8a7d2e4b90","order_id":"ef696416-5bf3-450d-a17a-4146423f43ab","occurred_at_utc":"2026-10-23T05:12:44.8123456+00:00","lines":[{"product_id":"6963107d-f9a8-4d2b-9a05-bfd44a3673dc","quantity":2}]}
            """;

        var parsed = JsonSerializer.Deserialize<StockReserved>(snakeCased, OrderPlaced.JsonOptions);

        parsed.Should().NotBeNull("unknown fields are ignored rather than rejected, which is its own risk");

        parsed!.OrderId.Should().Be(Guid.Empty,
            "the camelCase contract must not read snake_case fields; if this ever stops being empty the " +
            "two services have drifted and the consumer is settling orders it cannot identify");

        // `lines` and `quantity` are spelled identically in both casings, so they still bind — which
        // is what makes the drift silent rather than loud. One field reads correctly, the one that
        // says what to reconcile does not, and nothing throws.
        parsed.Lines.Should().ContainSingle();
        parsed.Lines[0].Quantity.Should().Be(2);
        parsed.Lines[0].ProductId.Should().Be(Guid.Empty);
    }
}
