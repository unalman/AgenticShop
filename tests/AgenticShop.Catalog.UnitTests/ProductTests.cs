using System.Globalization;
using AgenticShop.Catalog.Domain;
using FluentAssertions;

namespace AgenticShop.Catalog.UnitTests;

public class ProductTests
{
    [Fact]
    public void Create_PopulatesDefaults()
    {
        var before = DateTimeOffset.UtcNow;

        var product = Product.Create("SKU-1", "Espresso Machine", "Dual boiler.", 199.99m);

        var after = DateTimeOffset.UtcNow;

        product.Id.Should().NotBeEmpty();
        product.Sku.Should().Be("SKU-1");
        product.Name.Should().Be("Espresso Machine");
        product.Description.Should().Be("Dual boiler.");
        product.Price.Should().Be(199.99m);
        product.Currency.Should().Be(Product.DefaultCurrency);
        product.IsActive.Should().BeTrue();
        product.CreatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        product.UpdatedAtUtc.Should().Be(product.CreatedAtUtc);
    }

    [Fact]
    public void Create_TrimsSurroundingWhitespace()
    {
        var product = Product.Create("  SKU-1  ", "  Espresso  ", "  Dual boiler.  ", 1m);

        product.Sku.Should().Be("SKU-1");
        product.Name.Should().Be("Espresso");
        product.Description.Should().Be("Dual boiler.");
    }

    [Fact]
    public void Create_TreatsBlankDescriptionAsAbsent()
    {
        Product.Create("SKU-1", "Espresso", null, 1m).Description.Should().BeNull();
        Product.Create("SKU-1", "Espresso", "   ", 1m).Description.Should().BeNull();
    }

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" eur ", "EUR")]
    [InlineData("TRY", "TRY")]
    [InlineData("gbp", "GBP")]
    [InlineData("XAU", "XAU")]
    // This machine's culture is Turkish, where ToUpper() maps 'i' to a dotted capital I
    // that is not an ASCII letter. ToUpperInvariant must be used, and this case is what
    // would fail if it ever reverted.
    [InlineData("ils", "ILS")]
    public void Create_NormalizesCurrencyToUpperInvariant(string input, string expected)
        => Product.Create("SKU-1", "Espresso", null, 1m, input).Currency.Should().Be(expected);

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("EURO")]
    public void Create_RejectsCurrencyThatIsNotThreeCharacters(string currency)
    {
        Action act = () => Product.Create("SKU-1", "Espresso", null, 1m, currency);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("US1")]
    [InlineData("U$D")]
    [InlineData("!!!")]
    [InlineData("U S")]
    [InlineData("ÜSD")]
    public void Create_RejectsThreeCharactersThatAreNotAsciiLetters(string currency)
    {
        // The rule used to be a bare length check, so all of these were accepted as
        // ISO 4217 codes and persisted to the varchar(3) currency column.
        Action act = () => Product.Create("SKU-1", "Espresso", null, 1m, currency);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_AcceptsEveryAsciiLetterPositionInACode()
    {
        // Guards against an over-tight rule that would reject legitimate codes.
        foreach (var code in new[] { "AAA", "zzz", "Mno", "XBT" })
        {
            var act = () => Product.Create("SKU-1", "Espresso", null, 1m, code);

            act.Should().NotThrow();
        }
    }

    [Theory]
    [InlineData("10.004", "10.00")]
    [InlineData("10.005", "10.01")]
    [InlineData("10.999", "11.00")]
    public void Create_RoundsPriceToTwoDecimalPlacesAwayFromZero(string input, string expected)
        => Product.Create("SKU-1", "Espresso", null, Parse(input))
            .Price.Should().Be(Parse(expected));

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsBlankSku(string? sku)
    {
        Action act = () => Product.Create(sku!, "Espresso", null, 1m);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsBlankName(string? name)
    {
        Action act = () => Product.Create("SKU-1", name!, null, 1m);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public void Create_RejectsNegativePrice(double price)
    {
        Action act = () => Product.Create("SKU-1", "Espresso", null, (decimal)price);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Update_ChangesMutableFields_AndPreservesIdentity()
    {
        var product = Product.Create("SKU-1", "Espresso", "Old", 10m, "USD");
        var createdAt = product.CreatedAtUtc;

        product.Update("Espresso Pro", "New", 25.5m);

        product.Sku.Should().Be("SKU-1");
        product.Currency.Should().Be("USD");
        product.CreatedAtUtc.Should().Be(createdAt);
        product.Name.Should().Be("Espresso Pro");
        product.Description.Should().Be("New");
        product.Price.Should().Be(25.5m);
        product.UpdatedAtUtc.Should().BeOnOrAfter(createdAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Update_RejectsBlankName(string? name)
    {
        var product = Product.Create("SKU-1", "Espresso", null, 10m);

        Action act = () => product.Update(name!, null, 20m);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public void Update_RejectsNegativePrice(double price)
    {
        var product = Product.Create("SKU-1", "Espresso", null, 10m);

        Action act = () => product.Update("Espresso Pro", null, (decimal)price);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Update_LeavesTheProductUntouchedWhenAGuardRejects()
    {
        // The guards must run before any mutation, otherwise a rejected update would leave
        // the entity half-changed and EF would persist the partial state on the next save.
        var product = Product.Create("SKU-1", "Espresso", "Original", 10m);
        var updatedAt = product.UpdatedAtUtc;

        Action act = () => product.Update("", null, -5m);

        act.Should().Throw<ArgumentException>();

        product.Name.Should().Be("Espresso");
        product.Description.Should().Be("Original");
        product.Price.Should().Be(10m);
        product.UpdatedAtUtc.Should().Be(updatedAt);
    }

    [Fact]
    public void Update_RoundsThePriceTheSameWayCreateDoes()
    {
        var product = Product.Create("SKU-1", "Espresso", null, 10m);

        product.Update("Espresso", null, Parse("25.005"));

        product.Price.Should().Be(Parse("25.01"));
    }

    [Fact]
    public void Update_TreatsBlankDescriptionAsAbsent()
    {
        var product = Product.Create("SKU-1", "Espresso", "Original", 10m);

        product.Update("Espresso", "   ", 10m);

        product.Description.Should().BeNull();
    }

    [Fact]
    public void Deactivate_ClearsIsActive()
    {
        var product = Product.Create("SKU-1", "Espresso", null, 1m);

        product.Deactivate();

        product.IsActive.Should().BeFalse();
    }
}
