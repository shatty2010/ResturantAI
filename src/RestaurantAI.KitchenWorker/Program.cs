using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using RestaurantAI.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetValue("Kitchen:PreparationSeconds", 5) is < 0 or > 3600)
    throw new InvalidOperationException("Kitchen:PreparationSeconds must be between 0 and 3600.");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddRestaurant(builder.Configuration);
builder.Services.AddSingleton<KitchenConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KitchenConsumer>());
var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/health/consumer", (KitchenConsumer consumer) => consumer.IsConsuming ? Results.Ok("Consuming") : Results.StatusCode(503));
app.Run();
