using AgenticShop.Stock.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Stock.Data;

public class StockDbContext(DbContextOptions<StockDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(StockDbContext).Assembly);
}
