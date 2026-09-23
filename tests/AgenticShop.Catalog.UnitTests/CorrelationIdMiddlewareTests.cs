using AgenticShop.Catalog.Middleware;
using FluentAssertions;

namespace AgenticShop.Catalog.UnitTests;

/// <summary>
/// The inbound id is echoed into response headers, into the ProblemDetails body and into
/// every log line for the request, so it is untrusted input from the moment it arrives.
/// </summary>
public class CorrelationIdMiddlewareTests
{
    private const string MintedPattern = "^[0-9a-f]{32}$";

    [Theory]
    [InlineData("7e7a0d13746049cd87af65157e2a2dd5")]
    [InlineData("correlation-abc-123")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("order_9f2c.1")]
    [InlineData("A")]
    public void AcceptsIdsWithinTheAllowedCharset(string value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).Should().BeTrue();

        CorrelationIdMiddleware.Resolve(value).Should().Be(value, "a usable id must pass through unchanged");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MintsAnIdWhenNoneIsSupplied(string? value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).Should().BeFalse();

        CorrelationIdMiddleware.Resolve(value).Should().MatchRegex(MintedPattern);
    }

    [Fact]
    public void AcceptsTheMaximumLengthButRejectsOneCharacterMore()
    {
        var atLimit = new string('a', CorrelationIdMiddleware.MaxLength);
        var overLimit = new string('a', CorrelationIdMiddleware.MaxLength + 1);

        CorrelationIdMiddleware.IsAcceptable(atLimit).Should().BeTrue();
        CorrelationIdMiddleware.IsAcceptable(overLimit).Should().BeFalse();
        CorrelationIdMiddleware.Resolve(overLimit).Should().MatchRegex(MintedPattern);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("tab\tchar")]
    [InlineData("semi;colon")]
    [InlineData("quote\"char")]
    [InlineData("colon:char")]
    [InlineData("slash/char")]
    [InlineData("unicode-ünicode")]
    [InlineData("emoji-😀")]
    public void RejectsCharactersThatCouldReshapeAHeaderOrALogLine(string value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).Should().BeFalse();

        // Replaced rather than rejected: a malformed correlation id does not make the
        // underlying request invalid, and refusing the call would turn a tracing concern
        // into an availability one.
        CorrelationIdMiddleware.Resolve(value).Should().MatchRegex(MintedPattern);
    }

    [Fact]
    public void MintedIdsAreDistinct()
        => CorrelationIdMiddleware.Mint().Should().NotBe(CorrelationIdMiddleware.Mint());

    [Fact]
    public void TheMintedFormatIsItselfAcceptable()
        => CorrelationIdMiddleware.IsAcceptable(CorrelationIdMiddleware.Mint())
            .Should().BeTrue("a minted id must satisfy the same rule it is minted under");
}
