using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RestaurantAI.Infrastructure;

public sealed class OutboxPublisher(IServiceScopeFactory scopes, RabbitMessaging rabbit, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = rabbit.Connect();
                using var channel = connection.CreateModel();
                RabbitMessaging.Declare(channel);
                channel.ConfirmSelect();
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
                    await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                    var pending = await db.OutboxMessages.FromSqlRaw("SELECT * FROM \"OutboxMessages\" WHERE \"PublishedAt\" IS NULL ORDER BY \"CreatedAt\" LIMIT 20 FOR UPDATE SKIP LOCKED")
                        .ToListAsync(stoppingToken);
                    foreach (var message in pending)
                    {
                        RabbitMessaging.PublishConfirmed(channel, message);
                        message.PublishedAt = DateTime.UtcNow;
                        logger.LogInformation("Published OrderCreated {EventId}", message.Id);
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox publication failed; pending events remain durable and will be retried");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
