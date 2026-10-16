using System.Text;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace RestaurantAI.Infrastructure;

public sealed class RabbitMessaging(IConfiguration configuration)
{
    public const string Exchange = "restaurant.orders";
    public const string Queue = "kitchen.orders";
    public const string RoutingKey = "order.created";

    public IConnection Connect() => new ConnectionFactory
    {
        HostName = configuration["RabbitMQ:Host"] ?? "localhost",
        UserName = configuration["RabbitMQ:Username"] ?? "restaurant",
        Password = configuration["RabbitMQ:Password"] ?? "restaurant_dev",
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true,
        DispatchConsumersAsync = true,
        RequestedHeartbeat = TimeSpan.FromSeconds(15),
        NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
    }.CreateConnection();

    public static void Declare(IModel channel)
    {
        channel.ExchangeDeclare(Exchange, ExchangeType.Direct, durable: true);
        channel.ExchangeDeclare("restaurant.orders.dead", ExchangeType.Direct, durable: true);
        channel.QueueDeclare("kitchen.orders.dead", true, false, false,
            new Dictionary<string, object> { ["x-queue-type"] = "quorum" });
        channel.QueueBind("kitchen.orders.dead", "restaurant.orders.dead", RoutingKey);
        channel.QueueDeclare(Queue, true, false, false, new Dictionary<string, object>
        {
            ["x-queue-type"] = "quorum", ["x-delivery-limit"] = 5,
            ["x-dead-letter-exchange"] = "restaurant.orders.dead",
            ["x-dead-letter-routing-key"] = RoutingKey
        });
        channel.QueueBind(Queue, Exchange, RoutingKey);
    }

    // Channels are owned by one publisher/consumer; never shared concurrently.
    public static void PublishConfirmed(IModel channel, OutboxMessage message)
    {
        var returned = false;
        void OnReturn(object? sender, RabbitMQ.Client.Events.BasicReturnEventArgs args) => returned = true;
        channel.BasicReturn += OnReturn;
        try
        {
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.Type = "OrderCreated";
            properties.MessageId = message.Id.ToString();
            channel.BasicPublish(Exchange, RoutingKey, true, properties, Encoding.UTF8.GetBytes(message.Payload));
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
            if (returned) throw new IOException("OrderCreated was returned as unroutable.");
        }
        finally { channel.BasicReturn -= OnReturn; }
    }
}
