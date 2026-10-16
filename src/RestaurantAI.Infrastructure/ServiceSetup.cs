using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RestaurantAI.Infrastructure;

public static class ServiceSetup
{
    public static IServiceCollection AddRestaurant(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<RestaurantDbContext>(options => options.UseNpgsql(config.GetConnectionString("Restaurant")
            ?? "Host=localhost;Port=55432;Database=restaurant;Username=restaurant;Password=restaurant_dev"));
        services.AddScoped<OrderService>();
        services.AddScoped<KitchenProcessor>();
        services.AddSingleton<RabbitMessaging>();
        services.AddHealthChecks().AddCheck<DependenciesHealthCheck>("dependencies", tags: ["ready"]);
        services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        return services;
    }
}

public sealed class DependenciesHealthCheck(IServiceScopeFactory scopes, RabbitMessaging rabbit) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken)) return HealthCheckResult.Unhealthy("Database unavailable");
            using var connection = rabbit.Connect();
            return connection.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("RabbitMQ unavailable");
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("Dependencies unavailable", ex); }
    }
}
