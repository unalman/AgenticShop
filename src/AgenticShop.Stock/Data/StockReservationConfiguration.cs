using AgenticShop.Stock.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Stock.Data;

public sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    /// <summary>Named explicitly because the exception handler branches on it.</summary>
    public const string UniqueOrderStockItemIndexName = "ix_stock_reservations_order_id_stock_item_id";

    /// <summary>Supports releasing every reservation for an order — the compensation path.</summary>
    public const string OrderIndexName = "ix_stock_reservations_order_id";

    public const string StatusForeignKeyName = "fk_stock_reservations_stock_items";

    private const int StatusMaxLength = 20;

    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.ToTable("stock_reservations", table =>
        {
            table.HasCheckConstraint("ck_stock_reservations_quantity_positive", "quantity > 0");

            // Adding a member to ReservationStatus without extending this list makes inserts
            // fail loudly, which is the intended behaviour: the schema and the enum are one
            // vocabulary.
            table.HasCheckConstraint(
                "ck_stock_reservations_status_known",
                "status IN ('Pending', 'Confirmed', 'Released')");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.StockItemId)
            .IsRequired();

        builder.Property(r => r.OrderId)
            .IsRequired();

        builder.Property(r => r.Quantity)
            .IsRequired();

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(StatusMaxLength)
            .IsRequired();

        builder.Property(r => r.CreatedAtUtc)
            .IsRequired();

        builder.Property(r => r.UpdatedAtUtc)
            .IsRequired();

        // Derived from Status; not a column.
        builder.Ignore(r => r.IsPending);

        // One hold per product per order. Because stock_items.product_id is itself unique,
        // stock_item_id is 1:1 with product_id, so this enforces the rule without
        // denormalising ProductId onto the reservation. It also gives reserve natural
        // idempotency: a retry conflicts instead of double-holding.
        builder.HasIndex(r => new { r.OrderId, r.StockItemId })
            .HasDatabaseName(UniqueOrderStockItemIndexName)
            .IsUnique();

        builder.HasIndex(r => r.OrderId)
            .HasDatabaseName(OrderIndexName);

        // A real foreign key is legitimate here: both tables are in Stock's own database. The
        // prohibition is on cross-service keys. WithMany() without a navigation keeps the graph
        // flat, and Restrict means a stock item cannot be dropped out from under its holds.
        builder.HasOne<StockItem>()
            .WithMany()
            .HasForeignKey(r => r.StockItemId)
            .HasConstraintName(StatusForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
