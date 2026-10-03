using AgenticShop.Ordering.Domain;
using FluentAssertions;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// <see cref="OrderIdempotencyKey"/>'s guards. The key is untrusted input that becomes a primary key
/// value and appears in log lines, so what it accepts is a security boundary and not a formality.
/// </summary>
public class OrderIdempotencyKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_StoresTheKeyAndStartsUncompleted()
    {
        var key = OrderIdempotencyKey.Create("basket-4711", Now);

        key.Key.Should().Be("basket-4711");
        key.CreatedAtUtc.Should().Be(Now);
        key.IsCompleted.Should().BeFalse("a fresh claim has recorded no outcome yet");
        key.OrderId.Should().BeNull();
        key.StatusCode.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsAMissingKey(string? key)
    {
        var act = () => OrderIdempotencyKey.Create(key!, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_AcceptsExactlyTheMaximumLength()
    {
        var key = new string('a', OrderIdempotencyKey.MaxKeyLength);

        OrderIdempotencyKey.Create(key, Now).Key.Should().Be(key);
    }

    [Fact]
    public void Create_RejectsOneCharacterOverTheMaximum()
    {
        var key = new string('a', OrderIdempotencyKey.MaxKeyLength + 1);

        var act = () => OrderIdempotencyKey.Create(key, Now);

        // The column is varchar(MaxKeyLength), so this guard is what keeps an over-long key from
        // reaching PostgreSQL and arriving as a 22001 the caller would see as a generic 400.
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("line\nbreak")]
    [InlineData("nul\0character")]
    [InlineData("semi;colon")]
    [InlineData("quote\"mark")]
    [InlineData("tab\there")]
    [InlineData("non-ascii-é")]
    [InlineData("emoji-🎉")]
    public void Create_RejectsCharactersThatCouldReshapeALogLineOrHeader(string key)
    {
        var act = () => OrderIdempotencyKey.Create(key, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("d94f8a1e6c2b4f07a5e3c1b9d7f5a3e1")]
    [InlineData("D94F8A1E-6C2B-4F07-A5E3-C1B9D7F5A3E1")]
    [InlineData("basket-4711")]
    [InlineData("order_2026.10.03_001")]
    [InlineData("a")]
    public void Create_AcceptsTheShapesCallersActuallyUse(string key)
    {
        OrderIdempotencyKey.Create(key, Now).Key.Should().Be(key);
    }

    [Fact]
    public void Complete_RecordsTheOrderAndTheStatusTheCallerWasGiven()
    {
        var key = OrderIdempotencyKey.Create("basket-4711", Now);
        var orderId = Guid.NewGuid();

        key.Complete(orderId, 201);

        key.IsCompleted.Should().BeTrue();
        key.OrderId.Should().Be(orderId);
        key.StatusCode.Should().Be(201);
        key.CreatedAtUtc.Should().Be(Now, "completing a claim does not rewrite when it was taken");
    }

    [Fact]
    public void Complete_Twice_Throws()
    {
        var key = OrderIdempotencyKey.Create("basket-4711", Now);
        key.Complete(Guid.NewGuid(), 201);

        var act = () => key.Complete(Guid.NewGuid(), 502);

        // Strict rather than idempotent. Only the request that won the claim can reach Complete, so
        // a second call means this assembly has a bug — and silently overwriting the recorded status
        // would corrupt every later replay of this key.
        act.Should().Throw<InvalidOperationException>();

        key.StatusCode.Should().Be(201, "the refused second call must not have changed anything");
    }

    [Fact]
    public void Complete_RejectsAnEmptyOrderId()
    {
        var key = OrderIdempotencyKey.Create("basket-4711", Now);

        var act = () => key.Complete(Guid.Empty, 201);

        act.Should().Throw<ArgumentException>();
        key.IsCompleted.Should().BeFalse("guards run before any mutation");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(-1)]
    public void Complete_RejectsAStatusThatIsNotAnHttpStatus(int statusCode)
    {
        var key = OrderIdempotencyKey.Create("basket-4711", Now);

        var act = () => key.Complete(Guid.NewGuid(), statusCode);

        act.Should().Throw<ArgumentOutOfRangeException>();
        key.IsCompleted.Should().BeFalse("guards run before any mutation");
    }

    [Fact]
    public void KeyCannotBeReassigned()
    {
        // The key is the row's identity and the caller's handle on it. A writable setter would let a
        // placement silently re-point a claim at a different key, which is how a duplicate order
        // would slip past the whole mechanism.
        typeof(OrderIdempotencyKey)
            .GetProperty(nameof(OrderIdempotencyKey.Key))!
            .GetSetMethod(nonPublic: false)
            .Should().BeNull("Key has no public setter");
    }
}
