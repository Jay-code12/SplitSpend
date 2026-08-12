using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.Exporter;
using Consul;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using SplitSpend.UserService.Application.Interfaces;
using SplitSpend.UserService.Application.Services;
using SplitSpend.UserService.Application.Validators;
using SplitSpend.UserService.Common;
using SplitSpend.UserService.Data;
using SplitSpend.UserService.Data.Repositories;
using SplitSpend.UserService.Domain.Events;
using SplitSpend.UserService.Infrastructure.Consul;
using SplitSpend.UserService.Infrastructure.Messaging;

namespace SplitSpend.UserService.Extensions;

public static class ServiceExtensions
{
    // ── Database ──────────────────────────────────────────────────────────────
    public static IServiceCollection AddDatabase(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<UserDbContext>(opts =>
            opts.UseSqlServer(
                config.GetConnectionString("UserDb"),
                sql =>
                {
                    sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
                    sql.CommandTimeout(30);
                }));

        services.AddScoped<IUserRepository, UserRepository>();
        return services;
    }

    // ── Application Services ──────────────────────────────────────────────────
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services)
    {
        services.AddScoped<IUserService,        Application.Services.UserService>();
        services.AddScoped<IUserEventPublisher, KafkaUserEventPublisher>();

        services.AddValidatorsFromAssemblyContaining<UpdateUserRequestValidator>();
        services.AddFluentValidationAutoValidation();
        return services;
    }

    // ── JWT (reads gateway-stamped claims, does not issue tokens) ─────────────
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services, JwtSettings jwt)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = true,
                    ValidateAudience         = true,
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer              = jwt.Issuer,
                    ValidAudience            = jwt.Audience,
                    IssuerSigningKey         =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization();
        return services;
    }

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    public static IServiceCollection AddUserRateLimiting(
        this IServiceCollection services)
    {
        services.AddRateLimiter(opts =>
        {
            // 300 req / 60s per UserId for all authenticated endpoints
            opts.AddPolicy("user-authenticated", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Request.Headers["X-User-Id"].FirstOrDefault()
                        ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window      = TimeSpan.FromSeconds(60),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            opts.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.StatusCode  = 429;
                ctx.HttpContext.Response.ContentType = "application/json";
                var correlationId =
                    ctx.HttpContext.Items["X-Correlation-Id"]?.ToString() ?? "unknown";

                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    ctx.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();

                await ctx.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    traceId = correlationId,
                    status  = 429,
                    error   = "TooManyRequests",
                    message = "Rate limit exceeded. Please try again shortly."
                }), ct);
            };
        });

        return services;
    }

    // ── OpenTelemetry ─────────────────────────────────────────────────────────
    public static IServiceCollection AddUserOpenTelemetry(
        this IServiceCollection services, OpenTelemetrySettings ot)
    {
        services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(ot.ServiceName, serviceVersion: ot.ServiceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] =
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                    ["service.component"] = "user"
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        opts.RecordException = true;
                        opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                        opts.EnrichWithHttpRequest = (activity, req) =>
                        {
                            if (req.Headers.TryGetValue("X-Correlation-Id", out var cid))
                                activity.SetTag("correlation.id", cid.ToString());
                            if (req.Headers.TryGetValue("X-User-Id", out var uid))
                                activity.SetTag("user.id", uid.ToString());
                        };
                    })
                    .AddHttpClientInstrumentation(opts => opts.RecordException = true)
                    .AddEntityFrameworkCoreInstrumentation(opts =>
                        opts.SetDbStatementForText = true)
                    .AddSource("MassTransit")
                    .AddOtlpExporter(opts => opts.Endpoint = new Uri(ot.OtlpEndpoint));

                if (!string.IsNullOrWhiteSpace(ot.AzureMonitorConnectionString))
                    tracing.AddAzureMonitorTraceExporter(opts =>
                        opts.ConnectionString = ot.AzureMonitorConnectionString);
            });

        return services;
    }

    // ── Serilog ───────────────────────────────────────────────────────────────
    public static WebApplicationBuilder AddUserSerilog(
        this WebApplicationBuilder builder, SeqSettings seq)
    {
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithEnvironmentName()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("ServiceName",    "SplitSpend.UserService")
                .Enrich.WithProperty("ServiceVersion", "1.0.0")
                .WriteTo.Console(
                    outputTemplate:
                        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(
                    path:                   "logs/user-.log",
                    rollingInterval:        RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] " +
                        "CorrelationId={CorrelationId} TraceId={TraceId} UserId={UserId} " +
                        "ServiceName={ServiceName} {Message:lj}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Warning)
                .WriteTo.Seq(
                    serverUrl:               seq.ServerUrl,
                    restrictedToMinimumLevel: LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft",                     LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("MassTransit",                   LogEventLevel.Information);
        });

        return builder;
    }

    // ── MassTransit / Kafka ───────────────────────────────────────────────────
    public static IServiceCollection AddKafkaMessaging(
        this IServiceCollection services, KafkaSettings kafka)
    {
        services.AddMassTransit(mt =>
        {
            mt.AddConsumer<UserRegisteredConsumer>();
            mt.UsingInMemory();

            mt.AddRider(rider =>
            {
                // Producers
                rider.AddProducer<string, UserCreatedEvent>(kafka.UserCreatedTopic);
                rider.AddProducer<string, UserUpdatedEvent>(kafka.UserUpdatedTopic);
                rider.AddProducer<string, UserDeletedEvent>(kafka.UserDeletedTopic);

                // Consumers
                rider.AddConsumer<UserRegisteredConsumer>();

                rider.UsingKafka((ctx, k) =>
                {
                    k.Host(kafka.BootstrapServers);

                    k.TopicEndpoint<string, UserRegisteredEvent>(
                        kafka.UserRegisteredTopic,
                        kafka.GroupId,
                        e =>
                        {
                            e.ConfigureConsumer<UserRegisteredConsumer>(ctx);
                            e.UseMessageRetry(r =>
                                r.Exponential(3,
                                    TimeSpan.FromSeconds(1),
                                    TimeSpan.FromSeconds(30),
                                    TimeSpan.FromSeconds(5)));
                            e.AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest;
                        });
                });
            });
        });

        return services;
    }

    // ── Consul ────────────────────────────────────────────────────────────────
    public static IServiceCollection AddConsulServiceDiscovery(
        this IServiceCollection services, ConsulSettings consul)
    {
        services.AddSingleton<IConsulClient>(_ =>
            new ConsulClient(cfg => cfg.Address = new Uri(consul.Host)));

        services.AddHostedService<ConsulRegistrationService>();
        return services;
    }

    // ── Health Checks ─────────────────────────────────────────────────────────
    public static IServiceCollection AddUserHealthChecks(
        this IServiceCollection services, IConfiguration config)
    {
        services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("User Service is running."),
                tags: ["live"])
            .AddSqlServer(
                config.GetConnectionString("UserDb")!,
                name:          "sqlserver",
                failureStatus: HealthStatus.Unhealthy,
                tags:          ["ready"]);

        return services;
    }

    public static WebApplication MapUserHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate      = c => c.Tags.Contains("live"),
            ResponseWriter = WriteJson
        });

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate      = c => c.Tags.Contains("ready"),
            ResponseWriter = WriteJson
        });

        return app;
    }

    private static Task WriteJson(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status  = report.Status.ToString(),
            entries = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description })
        }));
    }

    // ── Auto-migrate ──────────────────────────────────────────────────────────
    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<UserDbContext>>();
        try
        {
            logger.LogInformation("Applying database migrations...");
            await db.Database.MigrateAsync();
            logger.LogInformation("Migrations applied.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply database migrations.");
            throw;
        }
    }
}
