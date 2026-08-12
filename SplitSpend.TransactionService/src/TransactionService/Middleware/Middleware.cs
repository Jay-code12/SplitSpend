using System.Net;
using Consul;
using TransactionService.Domain.Entities;

namespace TransactionService.Middleware;

public class ExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlerMiddleware> _log;

    public ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> log)
    {
        _next = next;
        _log  = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (Exception ex) { await HandleAsync(ctx, ex); }
    }

    private async Task HandleAsync(HttpContext ctx, Exception ex)
    {
        var traceId = ctx.TraceIdentifier;

        var (status, message) = ex switch
        {
            TransactionNotFoundException      e => (HttpStatusCode.NotFound,    e.Message),
            TransactionNotOwnedException      e => (HttpStatusCode.Forbidden,   e.Message),
            TransactionDomainException        e => (HttpStatusCode.BadRequest,  e.Message),
            DuplicateIdempotencyKeyException  e => (HttpStatusCode.Conflict,    e.Message),
            ArgumentException                 e => (HttpStatusCode.BadRequest,  e.Message),
            _                                  => (HttpStatusCode.InternalServerError, "An unexpected error occurred.")
        };

        if (status == HttpStatusCode.InternalServerError)
            _log.LogError(ex, "Unhandled exception [TraceId={TraceId}]", traceId);
        else
            _log.LogWarning(ex, "[{Status}] [TraceId={TraceId}]: {Message}",
                (int)status, traceId, message);

        ctx.Response.StatusCode  = (int)status;
        ctx.Response.ContentType = "application/json";

        await ctx.Response.WriteAsJsonAsync(new
        {
            error   = message,
            traceId,
            status  = (int)status
        });
    }
}

// ── Consul ────────────────────────────────────────────────────────────────────

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
            ID      = $"transaction-service-{Environment.MachineName}",
            Name    = "transaction-service",
            Address = config["Service:Host"] ?? "localhost",
            Port    = int.Parse(config["Service:Port"] ?? "5006"),
            Tags    = new[] { "transaction", "finance" },
            Check   = new AgentServiceCheck
            {
                HTTP     = $"http://{config["Service:Host"] ?? "localhost"}:{config["Service:Port"] ?? "5006"}/health",
                Interval = TimeSpan.FromSeconds(10),
                Timeout  = TimeSpan.FromSeconds(5),
                DeregisterCriticalServiceAfter = TimeSpan.FromMinutes(1)
            }
        };

        lifetime.ApplicationStarted.Register(async () =>
        {
            await consul.Agent.ServiceRegister(registration);
            app.Logger.LogInformation("Registered transaction-service with Consul");
        });

        lifetime.ApplicationStopping.Register(async () =>
        {
            await consul.Agent.ServiceDeregister(registration.ID);
            app.Logger.LogInformation("Deregistered transaction-service from Consul");
        });
    }
}
