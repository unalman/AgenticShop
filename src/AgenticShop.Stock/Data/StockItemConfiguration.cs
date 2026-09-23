using AgenticShop.Stock.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Stock.Data;

public sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    /// <summary>
    /// Named explicitly rather than left to EF's convention, because the exception handler
    /// branches on it to produce an accurate conflict message.
    /// </summary>
    public const string UniqueProductIndexName = "ix_stock_items_product_id";

    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        // CHECK constraint SQL is passed through verbatim, so it must name the columns as
        // they exist after snake_case naming. PostgreSQL rejects a constraint that references
        // a column that does not exist, so a rename fails loudly at migration time.
        builder.ToTable("stock_items", table =>
        {
            // Defence in depth. StockItem already guards all three, and xmin prevents the
            // concurrent-write path from breaching them, so a violation here means this
            // assembly has a bug — which is why the handler reports it as a 500.
            table.HasCheckConstraint(
                "ck_stock_items_quantity_on_hand_non_negative",
                "quantity_on_hand >= 0");

            table.HasCheckConstraint(
                "ck_stock_items_reserved_non_negative",
                "reserved >= 0");

            table.HasCheckConstraint(
                "ck_stock_items_reserved_within_on_hand",
                "reserved <= quantity_on_hand");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.ProductId)
            .IsRequired();

        builder.HasIndex(s => s.ProductId)
            .HasDatabaseName(UniqueProductIndexName)
            .IsUnique();

        builder.Property(s => s.QuantityOnHand)
            .IsRequired();

        builder.Property(s => s.Reserved)
            .IsRequired();

        // Available is derived. Ignoring it keeps a second copy of the truth out of the schema.
        builder.Ignore(s => s.Available);

        builder.Property(s => s.UpdatedAtUtc)
            .IsRequired();

        // Optimistic concurrency via PostgreSQL's xmin system column. This is the mechanism
        // that stops two concurrent reservations from both succeeding against the same
        // stale Available count — on this entity, unlike Catalog's Product, a miss is
        // overselling rather than a lost price edit.
        //
        // UseXminAsConcurrencyToken() does not exist in Npgsql 10; the provider maps any uint
        // concurrency token generated OnAddOrUpdate to xmin, and the migrations SQL generator
        // suppresses the column so no DDL is emitted.
        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
