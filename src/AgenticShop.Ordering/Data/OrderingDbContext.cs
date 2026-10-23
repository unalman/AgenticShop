using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Ordering.Data;

public class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<OrderIdempotencyKey> IdempotencyKeys => Set<OrderIdempotencyKey>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Inbound deduplication. See <see cref="InboxMessage"/>'s remarks for why this one exists when
    /// the domain's strict transition would already make a redelivery harmless — it is the difference
    /// between logging a redelivery and logging a producer bug.
    /// </summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);
}
