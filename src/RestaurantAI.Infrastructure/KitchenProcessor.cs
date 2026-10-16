using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestaurantAI.Contracts;

namespace RestaurantAI.Infrastructure;

public sealed class KitchenProcessor(RestaurantDbContext db, IConfiguration configuration, ILogger<KitchenProcessor> logger)
{
    public async Task ProcessAsync(OrderCreated message, CancellationToken ct)
    {
        if (message.EventId == Guid.Empty || message.OrderId == Guid.Empty || message.CustomerId == Guid.Empty)
            throw new InvalidDataException("Invalid OrderCreated identifiers.");
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.OrderId, ct)
            ?? throw new InvalidDataException($"Order {message.OrderId} does not exist.");
        if (order.CustomerId != message.CustomerId) throw new InvalidDataException("Event customer does not match order.");
        if (order.Status == OrderStatus.Ready)
        {
            logger.LogInformation("Ignoring completed order {OrderId}, event {EventId}", message.OrderId, message.EventId);
            return;
        }
        // Conditional updates prevent regression even when deliveries overlap or a process restarts.
        await db.Orders.Where(x => x.Id == message.OrderId && x.Status == OrderStatus.Created)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Preparing)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
        logger.LogInformation("Preparing order {OrderId}", message.OrderId);
        await Task.Delay(TimeSpan.FromSeconds(configuration.GetValue("Kitchen:PreparationSeconds", 5)), ct);
        var changed = await db.Orders.Where(x => x.Id == message.OrderId && x.Status == OrderStatus.Preparing)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Ready)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
        if (changed > 0) logger.LogInformation("Order {OrderId} is Ready", message.OrderId);
    }
}
