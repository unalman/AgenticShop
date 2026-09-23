using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using AgenticShop.Catalog.Contracts;
using AgenticShop.Catalog.Data;
using AgenticShop.Catalog.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Catalog.UnitTests;

/// <summary>
/// Keeps the request contracts in step with the column limits they write to.
/// </summary>
/// <remarks>
/// This is the guard that would have prevented the original over-length-SKU 500. When a DTO
/// limit is looser than its column, the value reaches PostgreSQL and is rejected as a data
/// error; the handler now maps that to 400, but the caller gets no field-level detail. Keeping
/// the two aligned is what makes that mapping a backstop rather than the normal route.
/// </remarks>
public class ContractSchemaAlignmentTests
{
    [Fact]
    public void EveryStringColumnLimitIsMirroredByEveryContractThatCarriesIt()
    {
        var limits = StringColumnLimits();

        limits.Should().NotBeEmpty();

        var contracts = typeof(IRequestContract).Assembly
            .GetTypes()
            .Where(t => !t.IsInterface && typeof(IRequestContract).IsAssignableFrom(t))
            .ToList();

        contracts.Should().NotBeEmpty();

        var checkedProperties = 0;

        foreach (var contract in contracts)
        {
            foreach (var property in contract.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string) || !limits.TryGetValue(property.Name, out var columnLimit))
                {
                    continue;
                }

                checkedProperties++;

                DeclaredCap(property).Should().Be(
                    columnLimit,
                    $"{contract.Name}.{property.Name} writes to a varchar({columnLimit}) column, so the " +
                    "contract must not accept more than that or the database becomes the validator");
            }
        }

        checkedProperties.Should().BeGreaterThan(0, "the check must not pass vacuously");
    }

    [Fact]
    public void EveryDecimalColumnPrecisionIsRespectedByItsRangeAttribute()
    {
        // numeric(18,2) holds at most 9999999999999999.99. A looser [Range] would let an
        // out-of-range value through to PostgreSQL, which rejects it as 22003.
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;

        using var context = new CatalogDbContext(options);

        var price = context.Model
            .FindEntityType(typeof(Product))!
            .GetProperty(nameof(Product.Price));

        price.GetPrecision().Should().Be(18);
        price.GetScale().Should().Be(2);

        var cap = typeof(CreateProductRequest)
            .GetProperty(nameof(CreateProductRequest.Price))!
            .GetCustomAttribute<RangeAttribute>()!;

        Convert.ToDecimal(cap.Maximum, CultureInfo.InvariantCulture)
            .Should().BeLessThanOrEqualTo(9999999999999999.99m);
    }

    /// <summary>
    /// Reads the limits from the EF model rather than restating them, so the assertion tracks
    /// ProductConfiguration instead of a second copy that could itself drift.
    /// </summary>
    private static Dictionary<string, int> StringColumnLimits()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;

        using var context = new CatalogDbContext(options);

        return context.Model
            .FindEntityType(typeof(Product))!
            .GetProperties()
            .Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is int)
            .ToDictionary(p => p.Name, p => p.GetMaxLength()!.Value);
    }

    private static int? DeclaredCap(PropertyInfo property)
        => property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength
           ?? property.GetCustomAttribute<MaxLengthAttribute>()?.Length;
}
