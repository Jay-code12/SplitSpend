using BudgetService.Application.Interfaces;
using BudgetService.Application.Services;
using BudgetService.BackgroundJobs;
using BudgetService.Consumers;
using BudgetService.Infrastructure;
using BudgetService.Infrastructure.Data;
using BudgetService.Infrastructure.Messaging;
using BudgetService.Infrastructure.Repositories;
using BudgetService.Middleware;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using Serilog.Events;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ──────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Hangfire", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("ServiceName", "BudgetService")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{ServiceName}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File("logs/budget-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .WriteTo.Seq(builder.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341",
        apiKey: builder.Configuration["Seq:ApiKey"])
    .CreateLogger();

builder.Host.UseSerilog();

// ── Database (EF Core) ───────────────────────────────────────────────────────
builder.Services.AddDbContext<BudgetDbContext>(opts =>
    opts.UseSqlServer(
        builder.Configuration.GetConnectionString("BudgetDb"),
        sql => sql.EnableRetryOnFailure(maxRetryCount: 3)));

// ── Hangfire (CRON jobs) ─────────────────────────────────────────────────────
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(
        builder.Configuration.GetConnectionString("BudgetDb"),
        new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout       = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout   = TimeSpan.FromMinutes(5),
            QueuePollInterval            = TimeSpan.Zero,
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks           = true
        }));

builder.Services.AddHangfireServer(opts =>
{
    opts.WorkerCount   = 2;   // Budget CRON jobs are low-concurrency
    opts.Queues        = new[] { "budget-cron", "default" };
    opts.ServerTimeout = TimeSpan.FromMinutes(10);
});

// ── Repositories ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<IBudgetRepository,      BudgetRepository>();
builder.Services.AddScoped<IDailyBudgetRepository, DailyBudgetRepository>();
builder.Services.AddScoped<IGiftBudgetRepository,  GiftBudgetRepository>();
builder.Services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

// ── Application Services ──────────────────────────────────────────────────────
builder.Services.AddScoped<BudgetApplicationService>();
builder.Services.AddScoped<DailyCronService>();
builder.Services.AddScoped<DailyCronJob>();          // Hangfire job wrapper

// ── Wallet Service HTTP Client (with Polly resilience) ───────────────────────
builder.Services.AddHttpClient<IWalletServiceClient, WalletServiceClient>(client =>
{
    // Base URL resolved from Consul at startup, or from config in dev
    client.BaseAddress = new Uri(
        builder.Configuration["WalletService:BaseUrl"] ?? "http://localhost:5003/");
    client.Timeout = TimeSpan.FromSeconds(10);
})
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(300 * attempt),
        onRetry: (outcome, timespan, attempt, _) =>
            Log.Warning("Wallet Service retry {Attempt}/3 after {Delay}ms: {Reason}",
                attempt, timespan.TotalMilliseconds, outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString())))
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .CircuitBreakerAsync(
        handledEventsAllowedBeforeBreaking: 5,
        durationOfBreak: TimeSpan.FromSeconds(30),
        onBreak: (_, duration) =>
            Log.Error("Circuit breaker OPEN for Wallet Service for {Duration}s", duration.TotalSeconds),
        onReset: () =>
            Log.Information("Circuit breaker CLOSED — Wallet Service recovered")));

// ── Kafka ─────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IKafkaPublisher, KafkaPublisher>();

// ── Kafka Consumers (background services) ────────────────────────────────────
builder.Services.AddHostedService<WalletBudgetTransferCompletedConsumer>();
builder.Services.AddHostedService<WalletBudgetTransferFailedConsumer>();
builder.Services.AddHostedService<WalletBudgetDebitedConsumer>();

// ── Hangfire Job Registrar ────────────────────────────────────────────────────
builder.Services.AddHostedService<HangfireJobRegistrar>();

// ── JWT Authentication ────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"],
            ValidAudience            = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("BudgetService"))
            .AddAspNetCoreInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(otlp =>
                otlp.Endpoint = new Uri(
                    builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317"));
    });

// ── Consul ────────────────────────────────────────────────────────────────────
builder.Services.AddConsul(builder.Configuration);

// ── Health Checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddSqlServer(builder.Configuration.GetConnectionString("BudgetDb")!)
    .AddKafka(new Confluent.Kafka.ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    })
    .AddUrlGroup(
        new Uri(builder.Configuration["WalletService:BaseUrl"] + "health"),
        name: "wallet-service",
        tags: new[] { "dependency" });

// ── MVC + Swagger ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SplitSpend – Budget Service", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = Microsoft.OpenApi.Models.ParameterLocation.Header
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Auto-migrate on startup (dev/staging only)
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<ExceptionHandlerMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging(opts =>
{
    opts.EnrichDiagnosticContext = (diag, ctx) =>
    {
        diag.Set("UserId",    ctx.User.FindFirst("sub")?.Value ?? "anonymous");
        diag.Set("RequestId", ctx.TraceIdentifier);
    };
});

// Hangfire Dashboard (admin-only in production)
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthFilter() }
});

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

await app.RegisterWithConsulAsync(app.Configuration);

app.Logger.LogInformation("BudgetService starting on port {Port}",
    builder.Configuration["Service:Port"] ?? "5004");

app.Run();

// ── Hangfire dashboard auth filter ───────────────────────────────────────────
public class HangfireAuthFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext ctx)
    {
        var httpCtx = ctx.GetHttpContext();
        // In production: require Admin role
        return httpCtx.User.IsInRole("Admin") || httpCtx.RequestServices
            .GetRequiredService<IWebHostEnvironment>().IsDevelopment();
    }
}
