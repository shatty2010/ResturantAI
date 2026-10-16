using RestaurantAI.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using RestaurantAI.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddRestaurant(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddHostedService<OutboxPublisher>();
var app = builder.Build();
if (app.Configuration.GetValue("Database:ApplyMigrations", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<RestaurantDbContext>().Database.MigrateAsync();
}
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/api/menu", async (OrderService orders, CancellationToken ct) => Results.Ok(await orders.GetMenuAsync(ct)))
    .WithName("GetMenu").Produces<List<MenuItemResponse>>();
app.MapPost("/api/orders", async (CreateOrderRequest request, OrderService orders, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        var order = await orders.CreateAsync(request, ct);
        logger.LogInformation("Created order {OrderId} for customer {CustomerId}, total {TotalAmount}", order.Id, order.CustomerId, order.TotalAmount);
        return Results.Created($"/api/orders/{order.Id}", order);
    }
    catch (OrderValidationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = [ex.Message] }); }
}).WithName("CreateOrder").Produces<OrderResponse>(201).ProducesValidationProblem();
app.MapGet("/api/orders/{id:guid}", async (Guid id, OrderService orders, CancellationToken ct) =>
    await orders.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound())
    .WithName("GetOrderStatus").Produces<OrderResponse>().Produces(404);
app.Run();
public partial class Program;
