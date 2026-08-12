using Consul;
using SplitSpend.Gateway.Configuration;
using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Services;

// ── Interface ─────────────────────────────────────────────────────────────────
public interface IConsulService
{
    Task<ConsulServiceEntry?> ResolveAsync(string serviceName, CancellationToken ct = default);
    Task RegisterGatewayAsync(CancellationToken ct = default);
    Task DeregisterGatewayAsync(CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────
public sealed class ConsulService(
    IConsulClient       consulClient,
    ConsulSettings      settings,
    ILogger<ConsulService> logger) : IConsulService
{
    private string? _serviceId;

    /// <summary>
    /// Resolves a healthy service instance from Consul using round-robin over
    /// the returned healthy entries. Returns null if no healthy instance exists.
    /// </summary>
    public async Task<ConsulServiceEntry?> ResolveAsync(
        string serviceName, CancellationToken ct = default)
    {
        var result = await consulClient.Health.Service(serviceName, string.Empty, true, ct);

        if (!result.Response.Any())
        {
            logger.LogWarning(
                "No healthy instances found in Consul for service={ServiceName}", serviceName);
            return null;
        }

        // Simple round-robin: pick a random healthy instance
        var entries = result.Response;
        var picked  = entries[Random.Shared.Next(entries.Length)];

        var address = string.IsNullOrWhiteSpace(picked.Service.Address)
            ? picked.Node.Address
            : picked.Service.Address;

        return new ConsulServiceEntry
        {
            ServiceName = serviceName,
            Address     = address,
            Port        = picked.Service.Port
        };
    }

    /// <summary>
    /// Registers this gateway instance with Consul so other services can
    /// discover it (e.g. for internal health dashboards).
    /// </summary>
    public async Task RegisterGatewayAsync(CancellationToken ct = default)
    {
        _serviceId = $"{settings.ServiceName}-{Guid.NewGuid():N}";

        var registration = new AgentServiceRegistration
        {
            ID      = _serviceId,
            Name    = settings.ServiceName,
            Port    = settings.ServicePort,
            Tags    = ["gateway", "api", "splitspend"],
            Check   = new AgentServiceCheck
            {
                HTTP                            = $"http://localhost:{settings.ServicePort}{settings.HealthCheckPath}",
                Interval                        = settings.HealthCheckInterval,
                Timeout                         = settings.HealthCheckTimeout,
                DeregisterCriticalServiceAfter  = settings.DeregisterCriticalServiceAfter
            }
        };

        await consulClient.Agent.ServiceRegister(registration, ct);

        logger.LogInformation(
            "Gateway registered with Consul. ServiceId={ServiceId} Port={Port}",
            _serviceId, settings.ServicePort);
    }

    /// <summary>
    /// Deregisters this gateway from Consul on graceful shutdown.
    /// </summary>
    public async Task DeregisterGatewayAsync(CancellationToken ct = default)
    {
        if (_serviceId is null) return;

        await consulClient.Agent.ServiceDeregister(_serviceId, ct);

        logger.LogInformation(
            "Gateway deregistered from Consul. ServiceId={ServiceId}", _serviceId);
    }
}

// ── Consul Lifetime Host ──────────────────────────────────────────────────────
/// <summary>
/// IHostedService that registers the gateway with Consul on startup and
/// deregisters it cleanly on shutdown.
/// </summary>
public sealed class ConsulLifetimeService(
    IConsulService consul,
    ILogger<ConsulLifetimeService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await consul.RegisterGatewayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register gateway with Consul on startup.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await consul.DeregisterGatewayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deregister gateway from Consul on shutdown.");
        }
    }
}
