using AgenticShop.Stock.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Stock.Data;

public class StockDbContext(DbContextOptions<StockDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    /// <summary>Inbound deduplication; see <see cref="InboxMessage"/>'s remarks for why the reservation unique index is not enough.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>Outbound events awaiting the broker; written in the same transaction as the reservation.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(StockDbContext).Assembly);
}
