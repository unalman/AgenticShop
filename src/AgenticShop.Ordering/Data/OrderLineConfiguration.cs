using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Ordering.Data;

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    /// <summary>
    /// Named explicitly because the exception handler branches on it. It also makes the
    /// "one line per product" rule a schema fact rather than only a domain one, mirroring Stock's
    /// <c>UNIQUE(order_id, stock_item_id)</c> — which is the constraint that forces the rule in the
    /// first place, since a second hold for the same product on the same order would be rejected.
    /// </summary>
    public const string UniqueOrderProductIndexName = "ix_order_lines_order_id_product_id";

    /// <summary>Supports reading an order's lines by order id without a scan.</summary>
    public const string OrderIndexName = "ix_order_lines_order_id";

    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines", table =>
        {
            table.HasCheckConstraint("ck_order_lines_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("ck_order_lines_unit_price_non_negative", "unit_price >= 0");
        });

        builder.HasKey(l => l.Id);

        builder.Property(l => l.OrderId)
            .IsRequired();

        builder.Property(l => l.ProductId)
            .IsRequired();

        builder.Property(l => l.ProductName)
            .HasMaxLength(OrderLine.ProductNameMaxLength)
            .IsRequired();

        builder.Property(l => l.Quantity)
            .IsRequired();

        builder.Property(l => l.UnitPrice)
            .HasPrecision(18, 2);

        // Derived from UnitPrice and Quantity; not a column.
        builder.Ignore(l => l.LineTotal);

        builder.HasIndex(l => new { l.OrderId, l.ProductId })
            .HasDatabaseName(UniqueOrderProductIndexName)
            .IsUnique();

        builder.HasIndex(l => l.OrderId)
            .HasDatabaseName(OrderIndexName);

        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
