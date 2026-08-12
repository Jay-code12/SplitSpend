using Consul;
using SplitSpend.AuthService.Settings;

namespace SplitSpend.AuthService.Infrastructure.Consul;

public sealed class ConsulRegistrationService(
    IConsulClient        consulClient,
    ConsulSettings       settings,
    ILogger<ConsulRegistrationService> logger) : IHostedService
{
    private string? _serviceId;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceId = $"{settings.ServiceName}-{Guid.NewGuid():N}";

        var registration = new AgentServiceRegistration
        {
            ID   = _serviceId,
            Name = settings.ServiceName,
            Port = settings.ServicePort,
            Tags = ["auth", "identity", "jwt", "splitspend"],
            Check = new AgentServiceCheck
            {
                HTTP                           = $"http://localhost:{settings.ServicePort}/health",
                Interval                       = settings.HealthCheckInterval,
                Timeout                        = settings.HealthCheckTimeout,
                DeregisterCriticalServiceAfter = settings.DeregisterCriticalServiceAfter
            }
        };

        try
        {
            await consulClient.Agent.ServiceRegister(registration, cancellationToken);
            logger.LogInformation(
                "Auth Service registered with Consul. ServiceId={ServiceId} Port={Port}",
                _serviceId, settings.ServicePort);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register Auth Service with Consul.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_serviceId is null) return;
        try
        {
            await consulClient.Agent.ServiceDeregister(_serviceId, cancellationToken);
            logger.LogInformation(
                "Auth Service deregistered from Consul. ServiceId={ServiceId}", _serviceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deregister Auth Service from Consul.");
        }
    }
}
