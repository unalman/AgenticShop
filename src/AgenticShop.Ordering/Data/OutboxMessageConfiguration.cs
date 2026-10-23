using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Ordering.Data;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <summary>
    /// Named explicitly rather than left to EF's convention. Nothing branches on it the way the
    /// exception handler branches on a unique index, but a named index is a stable target for the
    /// migration and for a test that wants to assert the poll query is supported.
    /// </summary>
    public const string UnpublishedIndexName = "ix_outbox_messages_published_at_utc";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        // No CHECK constraint here, deliberately. Order has one because its status is a vocabulary
        // that a migration and an enum must agree on, and Stock has them because its counters are
        // written by concurrent HTTP paths where a bad value is a real possibility. This row is
        // written by exactly one code path from values that path constructed, so a CHECK would
        // restate a guard the entity already ran and could only fire on a bug in our own code —
        // which §4.7 puts in the 5xx bucket either way. OrderIdempotencyKey, the closest analogue,
        // makes the same call.
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Exchange)
            .HasMaxLength(OutboxMessage.MaxExchangeLength)
            .IsRequired();

        builder.Property(m => m.RoutingKey)
            .HasMaxLength(OutboxMessage.MaxRoutingKeyLength)
            .IsRequired();

        // text, not jsonb. Nothing queries inside the payload, so jsonb would pay a parse on every
        // write and a binary rewrite on every read to buy an integrity check on JSON this assembly
        // serialised itself moments earlier.
        builder.Property(m => m.PayloadJson)
            .IsRequired();

        builder.Property(m => m.TraceParent)
            .HasMaxLength(OutboxMessage.MaxTraceParentLength);

        builder.Property(m => m.CreatedAtUtc)
            .IsRequired();

        // Nullable on purpose: null is the "committed but not yet handed to the broker" state, and
        // it is load-bearing rather than merely absent — it is what the publisher polls for and what
        // a crash between publish and acknowledgement leaves behind.
        builder.Property(m => m.PublishedAtUtc);

        // Derived from PublishedAtUtc; not a column.
        builder.Ignore(m => m.IsPublished);

        // The publisher's whole query is "the oldest rows that are still null", so this is the index
        // that keeps a poll from scanning a table that only ever grows.
        builder.HasIndex(m => m.PublishedAtUtc)
            .HasDatabaseName(UnpublishedIndexName);

        // Mandatory on every entity. Like the idempotency claim's, this token is not inert: the row
        // is inserted and then completed. One publisher owns the table today, so there is nothing to
        // conflict with — but the moment a second instance runs, the token is what turns two
        // publishers racing on one row into a 409 for the loser instead of a duplicate message
        // neither of them knows about.
        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
