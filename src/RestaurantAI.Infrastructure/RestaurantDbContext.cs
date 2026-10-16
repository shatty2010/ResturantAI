using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RestaurantAI.Infrastructure;

public sealed class RestaurantDbContext(DbContextOptions<RestaurantDbContext> options) : DbContext(options)
{
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MenuItem>().Property(x => x.Name).HasMaxLength(120);
        model.Entity<MenuItem>().Property(x => x.Description).HasMaxLength(500);
        model.Entity<MenuItem>().Property(x => x.Price).HasPrecision(12, 2);
        model.Entity<Order>().Property(x => x.TotalAmount).HasPrecision(12, 2);
        model.Entity<Order>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        model.Entity<OrderItem>().Property(x => x.UnitPrice).HasPrecision(12, 2);
        model.Entity<OrderItem>().Property(x => x.SpecialInstructions).HasMaxLength(500);
        model.Entity<OrderItem>().HasOne(x => x.MenuItem).WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Order>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId);
        model.Entity<OutboxMessage>().HasIndex(x => new { x.PublishedAt, x.CreatedAt });
        string[] names = ["Margherita Pizza", "Vegetable Burger", "Chicken Burger", "Pasta Alfredo", "Tomato Soup", "Garden Salad", "French Fries", "Grilled Chicken", "Chocolate Brownie", "Lemonade"];
        decimal[] prices = [12.50m, 8m, 10m, 13m, 6m, 7.50m, 4m, 16m, 5m, 3m];
        model.Entity<MenuItem>().HasData(names.Select((name, i) => new MenuItem
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:000000000000}"),
            Name = name, Description = $"Freshly prepared {name.ToLowerInvariant()}.", Price = prices[i]
        }));
    }
}

public sealed class RestaurantDbContextFactory : IDesignTimeDbContextFactory<RestaurantDbContext>
{
    public RestaurantDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<RestaurantDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Restaurant")
            ?? "Host=localhost;Port=55432;Database=restaurant;Username=restaurant;Password=restaurant_dev").Options);
}
