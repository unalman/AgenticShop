using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.RegularExpressions;
using AgenticShop.Stock.Contracts;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AgenticShop.Stock.UnitTests;

/// <summary>
/// Keeps the schema, the domain and the request contracts in step.
/// </summary>
/// <remarks>
/// Catalog's version of this suite compared DTO string limits against column limits. Stock has
/// almost no length-constrained strings, so copying it verbatim would have matched nothing and
/// passed without proving anything — the failure mode the non-vacuity guard exists to catch.
/// These tests target the drift that Stock can actually suffer: the enum vocabulary versus its
/// CHECK constraint, the quantity bounds versus the domain maximum, and the concurrency token
/// that stands between this service and overselling.
/// </remarks>
public class ContractSchemaAlignmentTests
{
    private const string UnusedConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    private static StockDbContext NewContext() => new(
        new DbContextOptionsBuilder<StockDbContext>().UseNpgsql(UnusedConnectionString).Options);

    [Fact]
    public void EveryEntityCarriesAnXminConcurrencyToken()
    {
        // The single most important schema convention in this service. Without the token two
        // concurrent reservations can both read the same stale Available count and both commit,
        // which is overselling — the failure the whole reservation design exists to prevent.
        // Asserting it per entity means a new Stock entity cannot forget it.
        using var db = NewContext();

        var entities = db.Model.GetEntityTypes().ToList();

        entities.Should().NotBeEmpty();

        foreach (var entity in entities)
        {
            var token = entity.FindProperty("xmin");

            token.Should().NotBeNull($"{entity.ClrType.Name} must be concurrency-protected");
            token!.IsConcurrencyToken.Should().BeTrue($"{entity.ClrType.Name}'s xmin must be a concurrency token");
            token.ClrType.Should().Be(typeof(uint), "Npgsql maps a uint token generated OnAddOrUpdate to the xmin system column");
        }
    }

    [Fact]
    public void TheStatusCheckConstraintListsExactlyTheEnumMembers()
    {
        // A new ReservationStatus member without a matching CHECK update makes every insert of
        // that status fail with SQLSTATE 23514, which the handler reports as a 500 — a server
        // fault caused purely by the schema lagging the enum.
        //
        // Check constraints are not part of EF's read-optimised runtime model, so this has to
        // go through IDesignTimeModel; GetCheckConstraints() on the runtime model throws.
        using var db = NewContext();

        var entity = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(StockReservation))!;

        var constraint = entity.GetCheckConstraints()
            .Should().ContainSingle(c => c.Sql.Contains("status", StringComparison.Ordinal))
            .Subject;

        var listed = Regex.Matches(constraint.Sql, "'([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToList();

        listed.Should().BeEquivalentTo(Enum.GetNames<ReservationStatus>(),
            "the CHECK vocabulary and the enum must be one list");
    }

    [Fact]
    public void TheStatusColumnIsWideEnoughForEveryEnumName()
    {
        using var db = NewContext();

        var status = db.Model.FindEntityType(typeof(StockReservation))!
            .FindProperty(nameof(StockReservation.Status))!;

        var longest = Enum.GetNames<ReservationStatus>().Max(name => name.Length);

        status.GetMaxLength().Should().BeGreaterThanOrEqualTo(longest);
    }

    [Fact]
    public void EveryQuantityBoundOnAContractMatchesTheDomainMaximum()
    {
        // If a contract admitted more than StockItem.MaxQuantity, the value would pass binding
        // and be rejected by the domain instead — a 400 with no field-level detail rather than
        // a validation problem naming the offending property.
        var checkedProperties = 0;

        foreach (var contract in RequestContracts())
        {
            foreach (var property in contract.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(int))
                {
                    continue;
                }

                var range = property.GetCustomAttribute<RangeAttribute>();

                range.Should().NotBeNull(
                    $"{contract.Name}.{property.Name} is a quantity and must be bounded, or it can " +
                    "overflow the counters the domain relies on");

                Convert.ToInt32(range!.Maximum).Should().Be(
                    StockItem.MaxQuantity,
                    $"{contract.Name}.{property.Name} must not admit more than the domain allows");

                Convert.ToInt32(range.Minimum).Should().BeGreaterThanOrEqualTo(0);

                checkedProperties++;
            }
        }

        checkedProperties.Should().BeGreaterThanOrEqualTo(3, "the check must not pass vacuously");
    }

    [Fact]
    public void TheDomainMaximumFitsTheIntegerColumn()
    {
        using var db = NewContext();

        foreach (var (type, property) in new[]
                 {
                     (typeof(StockItem), nameof(StockItem.QuantityOnHand)),
                     (typeof(StockItem), nameof(StockItem.Reserved)),
                     (typeof(StockReservation), nameof(StockReservation.Quantity))
                 })
        {
            var mapped = db.Model.FindEntityType(type)!.FindProperty(property)!;

            mapped.ClrType.Should().Be(typeof(int));
            StockItem.MaxQuantity.Should().BeLessThanOrEqualTo(int.MaxValue,
                $"{property} is an integer column, so the domain bound must fit it");
        }
    }

    [Fact]
    public void DerivedValuesAreNotPersisted()
    {
        // Available is QuantityOnHand - Reserved and IsPending is Status == Pending. Storing
        // either would create a second copy of the truth that could disagree with its inputs.
        using var db = NewContext();

        db.Model.FindEntityType(typeof(StockItem))!
            .FindProperty(nameof(StockItem.Available))
            .Should().BeNull();

        db.Model.FindEntityType(typeof(StockReservation))!
            .FindProperty(nameof(StockReservation.IsPending))
            .Should().BeNull();
    }

    [Fact]
    public void TheUniquenessRulesTheHandlerMessagesDependOnAreMapped()
    {
        // StockExceptionHandler keys its 409 messages on these names. If either index stops
        // being unique, or is renamed, the handler silently falls back to the generic message.
        using var db = NewContext();

        var productIndex = db.Model.FindEntityType(typeof(StockItem))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == StockItemConfiguration.UniqueProductIndexName);

        productIndex.IsUnique.Should().BeTrue();

        var reservationIndex = db.Model.FindEntityType(typeof(StockReservation))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == StockReservationConfiguration.UniqueOrderStockItemIndexName);

        reservationIndex.IsUnique.Should().BeTrue();
        reservationIndex.Properties.Select(p => p.Name)
            .Should().Equal(nameof(StockReservation.OrderId), nameof(StockReservation.StockItemId));
    }

    private static IEnumerable<Type> RequestContracts() => typeof(IRequestContract).Assembly
        .GetTypes()
        .Where(t => t.IsClass && !t.IsInterface && typeof(IRequestContract).IsAssignableFrom(t));
}
