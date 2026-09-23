using AgenticShop.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Catalog.Data;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <summary>
    /// Named explicitly rather than left to EF's convention, because the exception handler
    /// branches on it to produce an accurate conflict message.
    /// </summary>
    public const string UniqueSkuIndexName = "ix_products_sku";

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Sku)
            .HasMaxLength(Product.SkuMaxLength)
            .IsRequired();

        builder.HasIndex(p => p.Sku)
            .HasDatabaseName(UniqueSkuIndexName)
            .IsUnique();

        builder.Property(p => p.Name)
            .HasMaxLength(Product.NameMaxLength)
            .IsRequired();

        builder.Property(p => p.Description)
            .HasMaxLength(Product.DescriptionMaxLength);

        builder.Property(p => p.Price)
            .HasPrecision(18, 2);

        builder.Property(p => p.Currency)
            .HasMaxLength(Product.CurrencyLength)
            .IsRequired();

        // No database default. Product always assigns IsActive, and a store default on a
        // non-nullable bool makes `false` indistinguishable from "unset" to EF's sentinel
        // detection — an explicit false would be dropped from the INSERT and silently come
        // back as true.
        builder.Property(p => p.IsActive);

        // Optimistic concurrency via PostgreSQL's xmin system column. Without it two
        // concurrent updates both succeed and the second silently discards the first.
        // Npgsql maps any uint concurrency token generated OnAddOrUpdate to xmin, so this
        // costs no storage and needs no DDL — xmin already exists on every table.
        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();

        // Deactivated products are invisible by default. Callers that need them
        // (the catalog admin view) opt out with IgnoreQueryFilters().
        builder.HasQueryFilter(p => p.IsActive);
    }
}
