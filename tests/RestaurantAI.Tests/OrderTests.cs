using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using RestaurantAI.Contracts;
using RestaurantAI.Infrastructure;
using Xunit;

namespace RestaurantAI.Tests;

public sealed class OrderTests : IDisposable
{
    private readonly ApiFactory factory = new();
    private readonly HttpClient client;
    private static readonly Guid MenuId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public OrderTests() => client = factory.CreateClient();
    public void Dispose() { client.Dispose(); factory.Dispose(); }
    private static CreateOrderRequest Valid() => new(Guid.NewGuid(), [new(MenuId, 2, "No basil")]);

    [Fact]
    public async Task Menu_ReturnsTenAvailableSeededItems()
    {
        var menu = await client.GetFromJsonAsync<List<MenuItemResponse>>("/api/menu");
        Assert.Equal(10, menu!.Count);
        Assert.All(menu, item => Assert.True(item.Price > 0));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        await db.MenuItems.Where(x => x.Id == MenuId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAvailable, false));
        Assert.Equal(9, (await client.GetFromJsonAsync<List<MenuItemResponse>>("/api/menu"))!.Count);
    }

    [Fact]
    public async Task Create_PersistsOrderAndOutbox_AndReturnsLocation()
    {
        var response = await client.PostAsJsonAsync("/api/orders", Valid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal("Created", order.Status);
        Assert.Equal(25m, order.TotalAmount);
        Assert.Equal(12.5m, Assert.Single(order.Items).UnitPrice);
        Assert.Equal(DateTimeKind.Utc, order.CreatedAt.Kind);
        var fetched = await client.GetFromJsonAsync<OrderResponse>(response.Headers.Location);
        Assert.Equal(order.Id, fetched!.Id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        Assert.Equal(1, await db.Orders.CountAsync());
        var pending = await db.OutboxMessages.SingleAsync();
        Assert.Null(pending.PublishedAt);
        var message = JsonSerializer.Deserialize<OrderCreated>(pending.Payload, ContractJson.Options)!;
        Assert.Equal(order.Id, message.OrderId);
        Assert.Equal(pending.Id, message.EventId);
    }

    [Fact]
    public async Task ClientPrices_AreIgnored()
    {
        var json = $$"""{"customerId":"{{Guid.NewGuid()}}","totalAmount":0.01,"items":[{"menuItemId":"{{MenuId}}","quantity":2,"unitPrice":0.01}]}""";
        var response = await client.PostAsync("/api/orders", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(25m, (await response.Content.ReadFromJsonAsync<OrderResponse>())!.TotalAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task InvalidQuantity_IsRejected(int quantity) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", new CreateOrderRequest(Guid.NewGuid(), [new(MenuId, quantity)]))).StatusCode);

    [Fact]
    public async Task InvalidOrders_AreRejectedWithoutPersisting()
    {
        CreateOrderRequest[] requests = [new(Guid.Empty, [new(MenuId, 1)]), new(Guid.NewGuid(), []),
            new(Guid.NewGuid(), null), new(Guid.NewGuid(), [new(Guid.NewGuid(), 1)]),
            new(Guid.NewGuid(), [new(MenuId, 1, new string('x', 501))]),
            new(Guid.NewGuid(), Enumerable.Repeat(new CreateOrderItem(MenuId, 1), 51).ToList()),
            new(Guid.NewGuid(), [null!])];
        foreach (var request in requests)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", request)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task UnavailableMenuItem_IsRejected()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        await db.MenuItems.Where(x => x.Id == MenuId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAvailable, false));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", Valid())).StatusCode);
    }

    [Fact]
    public async Task MissingOrder_Returns404() => Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/orders/{Guid.NewGuid()}")).StatusCode);

    [Fact]
    public async Task Kitchen_TransitionsToReady_AndDuplicateDoesNotChangeTimestamp()
    {
        var response = await client.PostAsJsonAsync("/api/orders", Valid());
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<KitchenProcessor>();
        var message = new OrderCreated(Guid.NewGuid(), order.Id, order.CustomerId, order.CreatedAt);
        await processor.ProcessAsync(message, default);
        var ready = (await client.GetFromJsonAsync<OrderResponse>($"/api/orders/{order.Id}"))!;
        Assert.Equal("Ready", ready.Status);
        await processor.ProcessAsync(message, default);
        var duplicate = (await client.GetFromJsonAsync<OrderResponse>($"/api/orders/{order.Id}"))!;
        Assert.Equal("Ready", duplicate.Status);
        Assert.Equal(ready.UpdatedAt, duplicate.UpdatedAt);
    }

    [Fact]
    public async Task FailedOutboxInsert_RollsBackEntireOrder()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_outbox BEFORE INSERT ON OutboxMessages BEGIN SELECT RAISE(ABORT, 'simulated outbox failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<OrderService>().CreateAsync(Valid(), default));
        Assert.Equal(0, await db.Orders.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.OrderItems.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.OutboxMessages.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Kitchen_CancellationLeavesPreparing_ThenRedeliveryFinishes()
    {
        var response = await client.PostAsJsonAsync("/api/orders", Valid());
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kitchen:PreparationSeconds"] = "30" }).Build();
        var processor = new KitchenProcessor(db, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<KitchenProcessor>.Instance);
        var message = new OrderCreated(Guid.NewGuid(), order.Id, order.CustomerId, order.CreatedAt);
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(message, cancellation.Token);
        Assert.Equal(OrderStatus.Preparing, await db.Orders.AsNoTracking().Where(x => x.Id == order.Id).Select(x => x.Status).SingleAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        await scope.ServiceProvider.GetRequiredService<KitchenProcessor>().ProcessAsync(message, default);
        Assert.Equal(OrderStatus.Ready, await db.Orders.AsNoTracking().Where(x => x.Id == order.Id).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Kitchen_ResumesPreparingOrderAfterInterruption()
    {
        var response = await client.PostAsJsonAsync("/api/orders", Valid());
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        await db.Orders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Preparing));
        await scope.ServiceProvider.GetRequiredService<KitchenProcessor>().ProcessAsync(new(Guid.NewGuid(), order.Id, order.CustomerId, order.CreatedAt), default);
        Assert.Equal(OrderStatus.Ready, await db.Orders.Where(x => x.Id == order.Id).Select(x => x.Status).SingleAsync());
    }
}
