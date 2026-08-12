using System.Text;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.Exporter;
using Consul;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using SplitSpend.AuthService.Application.Validators;
using SplitSpend.AuthService.Settings;
using SplitSpend.AuthService.Data;
using SplitSpend.AuthService.Domain.Events;
using SplitSpend.AuthService.Infrastructure.Consul;
using SplitSpend.AuthService.Infrastructure.Messaging;
using FluentValidation;
using System.Text.Json;
using FluentValidation.AspNetCore;
using SplitSpend.AuthService.Repositories.AuthRepositores;
using SplitSpend.AuthService.Repositories.IAuthRepositores;
using SplitSpend.AuthService.Application.Services.AuthServices;
using SplitSpend.AuthService.Application.Services.IAuthServices;

namespace SplitSpend.AuthService.Extensions;

public static class ServiceExtensions
{
    // ── Database ──────────────────────────────────────────────────────────────
    public static IServiceCollection AddDatabase(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AuthDbContext>(opts =>
            opts.UseSqlServer(
                config.GetConnectionString("AuthDb"),
                sql =>
                {
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);
                    sql.CommandTimeout(30);
                }));

        // Repositories
        services.AddScoped<IUserCredentialRepository, UserCredentialRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IOtpRepository, OtpRepository>();

        return services;
    }

    // ── Application Services ──────────────────────────────────────────────────
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services)
    {
        services.AddScoped<IAuthService, Application.Services.AuthServices.AuthService>();
        services.AddScoped<ITokenService,   TokenService>();
        services.AddScoped<IOtpService,     OtpService>();
        services.AddScoped<IEventPublisher, KafkaEventPublisher>();

        // FluentValidation — auto-registers all validators in this assembly
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
        services.AddFluentValidationAutoValidation();

        return services;
    }

    // ── JWT Authentication ────────────────────────────────────────────────────
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

                opts.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        Log.Warning(
                            "JWT authentication failed: {Error}", ctx.Exception.Message);
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();
        return services;
    }

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    public static IServiceCollection AddAuthRateLimiting(
        this IServiceCollection services)
    {
        services.AddRateLimiter(opts =>
        {
            // Registration — 5/60s per IP
            opts.AddPolicy("auth-register", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window      = TimeSpan.FromSeconds(60),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            // Login — 5/60s per IP (brute-force guard)
            opts.AddPolicy("auth-login", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window      = TimeSpan.FromSeconds(60),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            // Forgot password — 3/60s per IP (prevents OTP spam)
            opts.AddPolicy("auth-forgot", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
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

                await ctx.HttpContext.Response.WriteAsync(
                    JsonSerializer.Serialize(new
                    {
                        traceId = correlationId,
                        status  = 429,
                        error   = "TooManyRequests",
                        message = "Too many attempts. Please wait before trying again."
                    }), ct);
            };
        });

        return services;
    }

    // ── OpenTelemetry ─────────────────────────────────────────────────────────
    public static IServiceCollection AddAuthOpenTelemetry(
        this IServiceCollection services, OpenTelemetrySettings otSettings)
    {
        services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(otSettings.ServiceName, serviceVersion: otSettings.ServiceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] =
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                    ["service.component"] = "auth"
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        opts.RecordException = true;
                        opts.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments("/health");
                        opts.EnrichWithHttpRequest = (activity, req) =>
                        {
                            if (req.Headers.TryGetValue("X-Correlation-Id", out var cid))
                                activity.SetTag("correlation.id", cid.ToString());
                        };
                    })
                    .AddHttpClientInstrumentation(opts => opts.RecordException = true)
                    .AddEntityFrameworkCoreInstrumentation(opts =>
                    {
                        // Capture DB command text (disable in prod for security)
                        opts.SetDbStatementForText = true;
                    })
                    .AddSource("MassTransit")   // capture Kafka producer/consumer spans
                    .AddOtlpExporter(opts =>
                        opts.Endpoint = new Uri(otSettings.OtlpEndpoint));

                if (!string.IsNullOrWhiteSpace(otSettings.AzureMonitorConnectionString))
                    tracing.AddAzureMonitorTraceExporter(opts =>
                        opts.ConnectionString = otSettings.AzureMonitorConnectionString);
            });

        return services;
    }

    // ── Serilog ───────────────────────────────────────────────────────────────
    public static WebApplicationBuilder AddAuthSerilog(
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
                .Enrich.WithProperty("ServiceName",    "SplitSpend.AuthService")
                .Enrich.WithProperty("ServiceVersion", "1.0.0")
                .WriteTo.Console(
                    outputTemplate:
                        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(
                    path:                  "logs/auth-.log",
                    rollingInterval:       RollingInterval.Day,
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
            // Register our consumer
            mt.AddConsumer<UserCreatedConsumer>();

            mt.UsingInMemory(); // needed even with Kafka rider

            mt.AddRider(rider =>
            {
                // ── Producers ─────────────────────────────────────────────
                rider.AddProducer<string, UserRegisteredEvent>(kafka.UserRegisteredTopic);
                rider.AddProducer<string, UserVerifiedEvent>  (kafka.UserVerifiedTopic);
                rider.AddProducer<string, UserLoggedInEvent>  (kafka.UserLoggedInTopic);

                // ── Consumers ─────────────────────────────────────────────
                rider.AddConsumer<UserCreatedConsumer>();

                rider.UsingKafka((ctx, k) =>
                {
                    k.Host(kafka.BootstrapServers);

                    // Consumer — user.created
                    k.TopicEndpoint<string, UserCreatedEvent>(
                        kafka.UserCreatedTopic,
                        kafka.GroupId,
                        e =>
                        {
                            e.ConfigureConsumer<UserCreatedConsumer>(ctx);

                            // Retry 3 times with exponential back-off before dead-lettering
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
    public static IServiceCollection AddAuthHealthChecks(
        this IServiceCollection services, IConfiguration config)
    {
        services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("Auth Service is running."),
                tags: ["live"])
            .AddSqlServer(
                config.GetConnectionString("AuthDb")!,
                name:          "sqlserver",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags:          ["ready"]);

        return services;
    }

    public static WebApplication MapAuthHealthChecks(this WebApplication app)
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

    // ── Auto-migrate DB on startup ────────────────────────────────────────────
    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AuthDbContext>>();

        try
        {
            logger.LogInformation("Applying database migrations...");
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply database migrations.");
            throw;
        }
    }
}
