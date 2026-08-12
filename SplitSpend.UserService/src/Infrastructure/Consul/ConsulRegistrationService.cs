using Consul;
using SplitSpend.UserService.Common;

namespace SplitSpend.UserService.Infrastructure.Consul;

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
            Tags = ["user", "profile", "splitspend"],
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
                "User Service registered with Consul. ServiceId={ServiceId} Port={Port}",
                _serviceId, settings.ServicePort);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register User Service with Consul.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_serviceId is null) return;
        try
        {
            await consulClient.Agent.ServiceDeregister(_serviceId, cancellationToken);
            logger.LogInformation(
                "User Service deregistered from Consul. ServiceId={ServiceId}", _serviceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deregister User Service from Consul.");
        }
    }
}
