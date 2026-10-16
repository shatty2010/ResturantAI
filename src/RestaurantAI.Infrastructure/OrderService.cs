using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantAI.Contracts;

namespace RestaurantAI.Infrastructure;

public sealed class OrderValidationException(string message) : Exception(message);

public sealed class OrderService(RestaurantDbContext db)
{
    public Task<List<MenuItemResponse>> GetMenuAsync(CancellationToken ct) => db.MenuItems.AsNoTracking()
        .Where(x => x.IsAvailable).OrderBy(x => x.Name)
        .Select(x => new MenuItemResponse(x.Id, x.Name, x.Description, x.Price)).ToListAsync(ct);

    public async Task<OrderResponse> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        if (request.CustomerId == Guid.Empty) throw new OrderValidationException("CustomerId is required.");
        if (request.Items is not { Count: > 0 and <= 50 }) throw new OrderValidationException("Provide 1 to 50 items.");
        if (request.Items.Any(x => x is null || x.MenuItemId == Guid.Empty || x.Quantity is < 1 or > 100 || x.SpecialInstructions?.Length > 500))
            throw new OrderValidationException("Each item requires a menu item ID, quantity 1–100, and instructions of at most 500 characters.");
        var ids = request.Items.Select(x => x.MenuItemId).Distinct().ToArray();
        var menu = await db.MenuItems.Where(x => ids.Contains(x.Id) && x.IsAvailable).ToDictionaryAsync(x => x.Id, ct);
        if (menu.Count != ids.Length) throw new OrderValidationException("One or more menu items are unknown or unavailable.");
        var now = DateTime.UtcNow;
        var order = new Order { Id = Guid.NewGuid(), CustomerId = request.CustomerId, CreatedAt = now, UpdatedAt = now };
        order.Items = request.Items.Select(x => new OrderItem
        {
            Id = Guid.NewGuid(), MenuItemId = x.MenuItemId, MenuItem = menu[x.MenuItemId],
            Quantity = x.Quantity, UnitPrice = menu[x.MenuItemId].Price, SpecialInstructions = x.SpecialInstructions?.Trim()
        }).ToList();
        order.TotalAmount = order.Items.Sum(x => x.UnitPrice * x.Quantity);
        var message = new OrderCreated(Guid.NewGuid(), order.Id, order.CustomerId, now);
        db.Orders.Add(order);
        db.OutboxMessages.Add(new OutboxMessage { Id = message.EventId, CreatedAt = now, Payload = JsonSerializer.Serialize(message, ContractJson.Options) });
        // A single relational SaveChanges transaction atomically commits the order and outbox.
        await db.SaveChangesAsync(ct);
        return Map(order);
    }

    public async Task<OrderResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Items).ThenInclude(x => x.MenuItem).SingleOrDefaultAsync(x => x.Id == id, ct);
        return order is null ? null : Map(order);
    }

    private static OrderResponse Map(Order order) => new(order.Id, order.CustomerId, order.Status.ToString(),
        order.TotalAmount, order.CreatedAt, order.UpdatedAt,
        order.Items.Select(x => new OrderItemResponse(x.MenuItemId, x.MenuItem.Name, x.Quantity, x.UnitPrice, x.SpecialInstructions)).ToList());
}
