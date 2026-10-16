using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RestaurantAI.Contracts;

namespace RestaurantAI.Infrastructure;

public sealed class KitchenConsumer(IServiceScopeFactory scopes, RabbitMessaging rabbit, ILogger<KitchenConsumer> logger) : BackgroundService
{
    public bool IsConsuming { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = rabbit.Connect();
                using var channel = connection.CreateModel();
                RabbitMessaging.Declare(channel);
                channel.BasicQos(0, 1, false);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ModelShutdown += (_, _) => completion.TrySetResult();
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_, delivery) =>
                {
                    try
                    {
                        var message = JsonSerializer.Deserialize<OrderCreated>(delivery.Body.Span, ContractJson.Options)
                            ?? throw new InvalidDataException("Empty event.");
                        for (var attempt = 1; ; attempt++)
                        {
                            try
                            {
                                using var scope = scopes.CreateScope();
                                await scope.ServiceProvider.GetRequiredService<KitchenProcessor>().ProcessAsync(message, stoppingToken);
                                break;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidDataException && attempt < 3)
                            {
                                logger.LogWarning(ex, "Kitchen attempt {Attempt} failed for {OrderId}", attempt, message.OrderId);
                                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), stoppingToken);
                            }
                        }
                        channel.BasicAck(delivery.DeliveryTag, false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Closing the channel leaves the delivery unacknowledged for recovery.
                        completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Kitchen delivery {DeliveryTag} failed; routing to dead-letter queue", delivery.DeliveryTag);
                        try { channel.BasicNack(delivery.DeliveryTag, false, requeue: false); }
                        catch (Exception nackError) { logger.LogWarning(nackError, "Channel unavailable; broker will redeliver"); completion.TrySetResult(); }
                    }
                };
                consumer.ConsumerCancelled += (_, _) => { completion.TrySetResult(); return Task.CompletedTask; };
                channel.BasicConsume(RabbitMessaging.Queue, false, consumer);
                IsConsuming = true;
                await completion.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Kitchen connection failed; reconnecting"); }
            finally { IsConsuming = false; }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
