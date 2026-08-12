using Consul;
using SplitSpend.Gateway.Configuration;
using SplitSpend.Gateway.Services;

namespace SplitSpend.Gateway.Extensions;

public static class ConsulExtensions
{
    /// <summary>
    /// Registers:
    ///   IConsulClient     — the Consul HTTP client (singleton)
    ///   IConsulService    — wrapper with service resolution and self-registration
    ///   ConsulLifetimeService — IHostedService that registers/deregisters on startup/shutdown
    /// </summary>
    public static IServiceCollection AddSplitSpendConsul(
        this IServiceCollection services,
        ConsulSettings          settings)
    {
        // Register Consul client as singleton — it manages its own connection pool
        services.AddSingleton<IConsulClient>(_ =>
            new ConsulClient(cfg =>
            {
                cfg.Address = new Uri(settings.Host);
            }));

        services.AddSingleton<IConsulService, ConsulService>();
        services.AddHostedService<ConsulLifetimeService>();

        return services;
    }
}
