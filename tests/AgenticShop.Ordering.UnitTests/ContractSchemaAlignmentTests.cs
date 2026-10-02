using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.RegularExpressions;
using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// Keeps the schema, the domain and the request contracts in step.
/// </summary>
/// <remarks>
/// Re-derived for the drift Ordering can actually suffer, not copied. Catalog's version compared DTO
/// string limits against column limits; Stock's compared an enum vocabulary against a CHECK list and
/// quantity bounds against a domain maximum. Ordering has both of those concerns and three more that
/// neither service had: a business number generated in code rather than by a sequence, a total that
/// is summed rather than supplied, and snapshot limits that mirror constants declared in two other
/// services which this assembly must not reference.
/// </remarks>
public class ContractSchemaAlignmentTests
{
    private const string UnusedConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    private static OrderingDbContext NewContext() => new(
        new DbContextOptionsBuilder<OrderingDbContext>().UseNpgsql(UnusedConnectionString).Options);

    private static Order NewOrder()
    {
        var id = Guid.NewGuid();

        return Order.Create(id, "USD", [OrderLine.Create(id, Guid.NewGuid(), "Widget", 10.00m, 1)]);
    }

    [Fact]
    public void EveryEntityCarriesAnXminConcurrencyToken()
    {
        // Mandatory on every entity. On Order it is currently inert, because Phase 0 has no update
        // path, but the token has to be there before Phase 2's saga starts driving an order through
        // several states — adding it then would mean a migration and a window without protection.
        using var db = NewContext();

        var entities = db.Model.GetEntityTypes().ToList();

        entities.Should().NotBeEmpty();

        foreach (var entity in entities)
        {
            var token = entity.FindProperty("xmin");

            token.Should().NotBeNull($"{entity.ClrType.Name} must be concurrency-protected");
            token!.IsConcurrencyToken.Should().BeTrue($"{entity.ClrType.Name}'s xmin must be a concurrency token");
            token.ClrType.Should().Be(typeof(uint),
                "Npgsql maps a uint token generated OnAddOrUpdate to the xmin system column");
        }
    }

    [Fact]
    public void TheStatusCheckConstraintListsExactlyTheEnumMembers()
    {
        // A new OrderStatus member without a matching CHECK update makes every insert of that status
        // fail with SQLSTATE 23514, which the handler reports as a 500 — a server fault caused
        // purely by the schema lagging the enum. PartiallyConfirmed was added late in planning, which
        // is exactly how such a member gets missed.
        //
        // Check constraints are not part of EF's read-optimised runtime model, so this has to go
        // through IDesignTimeModel; GetCheckConstraints() on the runtime model throws.
        using var db = NewContext();

        var entity = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(Order))!;

        var constraint = entity.GetCheckConstraints()
            .Should().ContainSingle(c => c.Sql.Contains("status", StringComparison.Ordinal))
            .Subject;

        var listed = Regex.Matches(constraint.Sql, "'([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToList();

        listed.Should().BeEquivalentTo(Enum.GetNames<OrderStatus>(),
            "the CHECK vocabulary and the enum must be one list");
    }

    [Fact]
    public void TheStatusColumnIsWideEnoughForEveryEnumName()
    {
        using var db = NewContext();

        var status = db.Model.FindEntityType(typeof(Order))!
            .FindProperty(nameof(Order.Status))!;

        var longest = Enum.GetNames<OrderStatus>().Max(name => name.Length);

        status.GetMaxLength().Should().BeGreaterThanOrEqualTo(longest);
        longest.Should().Be(nameof(OrderStatus.PartiallyConfirmed).Length,
            "the guard must be measuring the member that actually drives the width");
    }

    [Fact]
    public void TheQuantityBoundOnTheContractMatchesTheDomainMaximum()
    {
        // If the contract admitted more than OrderLine.MaxQuantity, the value would pass binding and
        // be rejected by the domain instead — a 400 with no field-level detail rather than a
        // validation problem naming Lines[0].Quantity.
        var range = typeof(CreateOrderLineRequest)
            .GetProperty(nameof(CreateOrderLineRequest.Quantity))!
            .GetCustomAttribute<RangeAttribute>();

        range.Should().NotBeNull("an unbounded quantity would be refused by Stock, not by us");
        Convert.ToInt32(range!.Maximum).Should().Be(OrderLine.MaxQuantity);
        Convert.ToInt32(range.Minimum).Should().Be(1, "an order line must ask for something");
    }

    [Fact]
    public void TheLineCountBoundOnTheContractMatchesTheDomainMaximum()
    {
        // Two independent bounds on the same collection, in two places, for two reasons: the
        // attribute gives field-level detail, the domain guard protects the invariant. If they
        // disagree the attribute is decorative.
        var lines = typeof(CreateOrderRequest).GetProperty(nameof(CreateOrderRequest.Lines))!;

        Convert.ToInt32(lines.GetCustomAttribute<MaxLengthAttribute>()!.Length).Should().Be(Order.MaxLines);
        Convert.ToInt32(lines.GetCustomAttribute<MinLengthAttribute>()!.Length).Should().Be(1);
    }

    [Fact]
    public void EverySnapshotLimitMatchesTheColumnItIsWrittenTo()
    {
        // These constants mirror declarations in Catalog and Stock, which this assembly must not
        // reference. Nothing enforces that they agree except this test and the fact that a longer
        // value could not have come from Catalog in the first place — so the column has to be the
        // one that is checked, because that is the side that fails.
        using var db = NewContext();

        var order = db.Model.FindEntityType(typeof(Order))!;
        var line = db.Model.FindEntityType(typeof(OrderLine))!;

        order.FindProperty(nameof(Order.Currency))!.GetMaxLength().Should().Be(Order.CurrencyLength);
        order.FindProperty(nameof(Order.OrderNumber))!.GetMaxLength().Should().Be(Order.OrderNumberMaxLength);
        line.FindProperty(nameof(OrderLine.ProductName))!.GetMaxLength().Should().Be(OrderLine.ProductNameMaxLength);
    }

    [Fact]
    public void AGeneratedOrderNumberFitsItsColumn()
    {
        // The number is produced in code, so nothing but this test stands between a format change
        // and a 22001 on every insert. Measured against a real one rather than a hand-counted
        // literal, so changing the prefix or the suffix length fails here first.
        using var db = NewContext();

        var column = db.Model.FindEntityType(typeof(Order))!
            .FindProperty(nameof(Order.OrderNumber))!;

        var generated = NewOrder().OrderNumber;

        generated.Length.Should().BeLessThanOrEqualTo(column.GetMaxLength()!.Value);
        generated.Length.Should().BeLessThanOrEqualTo(Order.OrderNumberMaxLength);
        generated.Should().MatchRegex(@"^ORD-\d{8}-[0-9a-f]{8}$");
    }

    [Fact]
    public void TheMaximumTotalFitsTheNumericColumn()
    {
        // Order.MaxTotalAmount exists so an overflowing total is a 400 raised before anything is
        // reserved, rather than a 22003 from PostgreSQL afterwards. It is only worth that if it
        // actually agrees with the column.
        using var db = NewContext();

        var total = db.Model.FindEntityType(typeof(Order))!
            .FindProperty(nameof(Order.TotalAmount))!;

        var precision = total.GetPrecision()!.Value;
        var scale = total.GetScale()!.Value;

        precision.Should().Be(18);
        scale.Should().Be(2);

        var largest = decimal.Parse(
            new string('9', precision - scale) + "." + new string('9', scale),
            System.Globalization.CultureInfo.InvariantCulture);

        Order.MaxTotalAmount.Should().BeLessThanOrEqualTo(largest);
    }

    [Fact]
    public void DerivedValuesAreNotPersisted()
    {
        // LineTotal is UnitPrice * Quantity and IsTerminal is Status != Pending. Storing either
        // would create a second copy of the truth that could disagree with its inputs.
        using var db = NewContext();

        db.Model.FindEntityType(typeof(OrderLine))!
            .FindProperty(nameof(OrderLine.LineTotal))
            .Should().BeNull();

        db.Model.FindEntityType(typeof(Order))!
            .FindProperty(nameof(Order.IsTerminal))
            .Should().BeNull();
    }

    [Fact]
    public void TheUniquenessRulesTheHandlerMessagesDependOnAreMapped()
    {
        // OrderingExceptionHandler keys its 409 messages on these names. If either index stops being
        // unique, or is renamed, the handler silently falls back to the generic message.
        using var db = NewContext();

        var orderNumberIndex = db.Model.FindEntityType(typeof(Order))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == OrderConfiguration.UniqueOrderNumberIndexName);

        orderNumberIndex.IsUnique.Should().BeTrue();

        var lineIndex = db.Model.FindEntityType(typeof(OrderLine))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == OrderLineConfiguration.UniqueOrderProductIndexName);

        lineIndex.IsUnique.Should().BeTrue();
        lineIndex.Properties.Select(p => p.Name)
            .Should().Equal(nameof(OrderLine.OrderId), nameof(OrderLine.ProductId));
    }

    [Fact]
    public void ALineCannotOutliveItsOrder()
    {
        // Cascade, where Stock's reservation → stock item key is Restrict. A line is part of the
        // order aggregate; a reservation is an audit trail that has to survive whatever happens to
        // the counter it was taken against. Getting this the wrong way round either orphans lines or
        // destroys history, so it is asserted rather than assumed.
        using var db = NewContext();

        var foreignKey = db.Model.FindEntityType(typeof(OrderLine))!
            .GetForeignKeys()
            .Should().ContainSingle().Subject;

        foreignKey.PrincipalEntityType.ClrType.Should().Be(typeof(Order));
        foreignKey.GetConstraintName().Should().Be(OrderConfiguration.LinesForeignKeyName);
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }
}
