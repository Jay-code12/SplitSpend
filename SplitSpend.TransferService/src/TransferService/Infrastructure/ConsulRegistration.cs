using Consul;

namespace TransferService.Infrastructure;

public static class ConsulRegistration
{
    public static IServiceCollection AddConsul(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IConsulClient, ConsulClient>(p =>
            new ConsulClient(cfg =>
                cfg.Address = new Uri(config["Consul:Address"] ?? "http://localhost:8500")));
        return services;
    }

    public static async Task RegisterWithConsulAsync(this WebApplication app, IConfiguration config)
    {
        var consul   = app.Services.GetRequiredService<IConsulClient>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        var registration = new AgentServiceRegistration
        {
            ID      = $"transfer-service-{Environment.MachineName}",
            Name    = "transfer-service",
            Address = config["Service:Host"] ?? "localhost",
            Port    = int.Parse(config["Service:Port"] ?? "5005"),
            Tags    = new[] { "transfer", "paystack", "finance" },
            Check   = new AgentServiceCheck
            {
                HTTP     = $"http://{config["Service:Host"] ?? "localhost"}:{config["Service:Port"] ?? "5005"}/health",
                Interval = TimeSpan.FromSeconds(10),
                Timeout  = TimeSpan.FromSeconds(5),
                DeregisterCriticalServiceAfter = TimeSpan.FromMinutes(1)
            }
        };

        lifetime.ApplicationStarted.Register(async () =>
        {
            await consul.Agent.ServiceRegister(registration);
            app.Logger.LogInformation("Registered transfer-service with Consul");
        });

        lifetime.ApplicationStopping.Register(async () =>
        {
            await consul.Agent.ServiceDeregister(registration.ID);
            app.Logger.LogInformation("Deregistered transfer-service from Consul");
        });
    }
}
