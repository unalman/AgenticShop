using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Ordering.Data;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <summary>
    /// Named explicitly rather than left to EF's convention, because the exception handler
    /// branches on it to produce an accurate conflict message.
    /// </summary>
    public const string UniqueOrderNumberIndexName = "ix_orders_order_number";

    /// <summary>
    /// Named explicitly for the same reason, and declared here rather than in
    /// <see cref="OrderLineConfiguration"/> because the relationship is navigated from this side:
    /// <see cref="Order.Lines"/> is a real collection, unlike Stock's reservation → stock item key,
    /// which deliberately has no navigation property at all.
    /// </summary>
    public const string LinesForeignKeyName = "fk_order_lines_orders";

    /// <summary>
    /// Wider than the longest current member (<c>PartiallyConfirmed</c>, 18) on purpose. Stock uses
    /// 20 for a 9-character vocabulary and would have to be migrated to add a longer member; the
    /// extra bytes cost nothing and the alignment guard asserts the longest name still fits.
    /// </summary>
    private const int StatusMaxLength = 30;

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        // CHECK constraint SQL is passed through verbatim, so it must name the columns as they
        // exist after snake_case naming.
        builder.ToTable("orders", table =>
        {
            // Defence in depth. Order already guards both, and xmin closes the concurrent-write
            // path, so a violation here means this assembly has a bug — which is why the handler
            // reports it as a 500 rather than a client error.
            table.HasCheckConstraint("ck_orders_total_amount_non_negative", "total_amount >= 0");

            // Adding a member to OrderStatus without extending this list makes inserts fail
            // loudly, which is the intended behaviour: the schema and the enum are one vocabulary.
            table.HasCheckConstraint(
                "ck_orders_status_known",
                "status IN ('Pending', 'Confirmed', 'Failed', 'PartiallyConfirmed')");
        });

        builder.HasKey(o => o.Id);

        builder.Property(o => o.OrderNumber)
            .HasMaxLength(Order.OrderNumberMaxLength)
            .IsRequired();

        builder.HasIndex(o => o.OrderNumber)
            .HasDatabaseName(UniqueOrderNumberIndexName)
            .IsUnique();

        builder.Property(o => o.Status)
            .HasConversion<string>()
            .HasMaxLength(StatusMaxLength)
            .IsRequired();

        builder.Property(o => o.Currency)
            .HasMaxLength(Order.CurrencyLength)
            .IsRequired();

        builder.Property(o => o.TotalAmount)
            .HasPrecision(18, 2);

        builder.Property(o => o.PlacedAtUtc)
            .IsRequired();

        // Derived from Status; not a column.
        builder.Ignore(o => o.IsTerminal);

        // Cascade, not Restrict: a line is part of the order aggregate and cannot outlive it.
        // Stock's reservation → stock item key is Restrict for the opposite reason — a reservation
        // is an audit trail that must survive whatever happens to the counter it was taken against.
        builder.HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.OrderId)
            .HasConstraintName(LinesForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        // Lines is exposed as IReadOnlyCollection over a readonly List<OrderLine>, so EF must
        // materialise through the field rather than the property.
        builder.Navigation(o => o.Lines)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Mandatory on every entity. On Order it is currently inert — Phase 0 has no update path,
        // because the row is written once and already terminal — but Phase 2's saga will drive an
        // order through several states and will need it.
        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
