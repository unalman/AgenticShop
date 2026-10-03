using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Ordering.Data;

public sealed class OrderIdempotencyKeyConfiguration : IEntityTypeConfiguration<OrderIdempotencyKey>
{
    /// <summary>
    /// Named explicitly rather than left to EF's convention, because the exception handler branches
    /// on it. A lost race for the same key surfaces as a <c>23505</c> on this name and is reported
    /// as "already in use" rather than as a generic conflict.
    /// </summary>
    public const string PrimaryKeyName = "pk_order_idempotency_keys";

    public void Configure(EntityTypeBuilder<OrderIdempotencyKey> builder)
    {
        builder.ToTable("order_idempotency_keys");

        // The key is the identity. There is no surrogate id, because the whole point of the row is
        // that a given key resolves to at most one order — a unique index on a separate primary key
        // would express the same thing less directly.
        builder.HasKey(k => k.Key)
            .HasName(PrimaryKeyName);

        builder.Property(k => k.Key)
            .HasMaxLength(OrderIdempotencyKey.MaxKeyLength)
            .ValueGeneratedNever();

        // Nullable on purpose: null is the "claimed but not completed" state, and it is load-bearing
        // rather than merely absent. See OrderIdempotencyKey's remarks.
        builder.Property(k => k.OrderId);

        builder.Property(k => k.StatusCode);

        builder.Property(k => k.CreatedAtUtc)
            .IsRequired();

        // Derived from OrderId; not a column.
        builder.Ignore(k => k.IsCompleted);

        // Mandatory on every entity. This is the one row in Ordering with a real update path — the
        // claim is inserted, then completed — so unlike Order's the token is not inert. Only the
        // request that won the claim can complete it, but the token is what turns a bug in that
        // reasoning into a 409 rather than a silently overwritten status code.
        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
