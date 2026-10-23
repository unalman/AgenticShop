using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Ordering.Data;

public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    /// <summary>
    /// Named explicitly rather than left to EF's convention, because the consumer branches on it: a
    /// 23505 on this constraint is the "already processed" signal that turns an Error into an
    /// Information. Matching on the SqlState alone would not distinguish it from any other unique
    /// violation in the same save — and this transaction also writes the order's transition, whose
    /// own constraint failures mean something entirely different.
    /// </summary>
    public const string PrimaryKeyName = "pk_inbox_messages";

    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");

        // The message id is the identity, so there is no surrogate.
        builder.HasKey(m => m.MessageId)
            .HasName(PrimaryKeyName);

        builder.Property(m => m.MessageId)
            .ValueGeneratedNever();

        builder.Property(m => m.EventType)
            .HasMaxLength(InboxMessage.MaxEventTypeLength)
            .IsRequired();

        builder.Property(m => m.ReceivedAtUtc)
            .IsRequired();

        // Mandatory on every entity and inert here, as on Order's neighbours: the row is written once
        // and never updated. Declared anyway, because the convention is that the token exists whether
        // or not today's code exercises it.
        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
