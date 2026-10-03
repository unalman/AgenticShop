using AgenticShop.Ordering.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Ordering.Data;

public class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<OrderIdempotencyKey> IdempotencyKeys => Set<OrderIdempotencyKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);
}
