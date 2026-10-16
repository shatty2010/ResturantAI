namespace RestaurantAI.Contracts;

public static class ContractJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}

public sealed record OrderCreated(Guid EventId, Guid OrderId, Guid CustomerId, DateTime CreatedAt);
public sealed record CreateOrderRequest(Guid CustomerId, List<CreateOrderItem>? Items);
public sealed record CreateOrderItem(Guid MenuItemId, int Quantity, string? SpecialInstructions = null);
public sealed record MenuItemResponse(Guid Id, string Name, string Description, decimal Price);
public sealed record OrderItemResponse(Guid MenuItemId, string Name, int Quantity, decimal UnitPrice, string? SpecialInstructions);
public sealed record OrderResponse(Guid Id, Guid CustomerId, string Status, decimal TotalAmount,
    DateTime CreatedAt, DateTime UpdatedAt, List<OrderItemResponse> Items);
