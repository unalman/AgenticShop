using AgenticShop.Stock.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgenticShop.Stock.Data;

public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    /// <summary>
    /// Named explicitly rather than left to EF's convention, because the consumer branches on it: a
    /// 23505 on this constraint is the "already processed" signal, and matching on the SqlState alone
    /// would confuse it with a duplicate reservation, which is a different fact with a different
    /// answer. The same reason <c>StockReservation.UniqueOrderStockItemIndexName</c> is named.
    /// </summary>
    public const string PrimaryKeyName = "pk_inbox_messages";

    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");

        // The message id is the identity, so there is no surrogate — a unique index on a separate
        // primary key would express "at most one row per message" less directly than making it the key.
        builder.HasKey(m => m.MessageId)
            .HasName(PrimaryKeyName);

        builder.Property(m => m.MessageId)
            .ValueGeneratedNever();

        builder.Property(m => m.EventType)
            .HasMaxLength(InboxMessage.MaxEventTypeLength)
            .IsRequired();

        builder.Property(m => m.ReceivedAtUtc)
            .IsRequired();

        // Mandatory on every entity, and inert here for the same reason Order's is: the row is
        // written once and never updated, so nothing can conflict on a write. Declared anyway,
        // because the convention is that the token exists whether or not today's code exercises it —
        // and because two consumers racing on one message id are separated by the primary key, not by
        // this, which is worth stating rather than leaving a reader to infer that the token is doing
        // work it is not.
        builder.Property<uint>("xmin")
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
