using Consul;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text;
using System.Text.Json;

namespace SplitSpend.Gateway.Extensions;

public static class HealthCheckExtensions
{
    private static readonly string[] AllServices =
    [
        "auth-service", "user-service", "wallet-service", "budget-service",
        "transfer-service", "transaction-service", "payment-service",
        "vendor-pay-service", "notification-service"
    ];

    /// <summary>
    /// Registers health checks:
    ///   /health        — liveness (gateway process is alive)
    ///   /health/ready  — readiness (Consul reachable + all downstream services healthy)
    /// </summary>
    public static IServiceCollection AddSplitSpendHealthChecks(
        this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("Gateway is running."),
                tags: ["live"])
            .AddCheck<ConsulHealthCheck>("consul",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready"]);

        return services;
    }

    public static WebApplication MapSplitSpendHealthChecks(this WebApplication app)
    {
        // Liveness — just "is the process alive?"
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate      = check => check.Tags.Contains("live"),
            ResponseWriter = WriteHealthJson
        });

        // Readiness — "is the gateway connected to its dependencies?"
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate      = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthJson
        });

        return app;
    }

    private static Task WriteHealthJson(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";

        var result = JsonSerializer.Serialize(new
        {
            status  = report.Status.ToString(),
            entries = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status      = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    duration    = e.Value.Duration.TotalMilliseconds
                })
        });

        return ctx.Response.WriteAsync(result, Encoding.UTF8);
    }
}

// ── Consul health check ───────────────────────────────────────────────────────
public sealed class ConsulHealthCheck(IConsulClient consulClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken  cancellationToken = default)
    {
        try
        {
            var result = await consulClient.Status.Ping(cancellationToken);
            return result.StatusCode == System.Net.HttpStatusCode.OK
                ? HealthCheckResult.Healthy("Consul is reachable.")
                : HealthCheckResult.Degraded("Consul returned unexpected status.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach Consul.", ex);
        }
    }
}
