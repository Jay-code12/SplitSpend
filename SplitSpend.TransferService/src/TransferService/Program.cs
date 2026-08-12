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
using TransferService.Application.Interfaces;
using TransferService.Application.Services;
using TransferService.BackgroundJobs;
using TransferService.Consumers;
using TransferService.Infrastructure;
using TransferService.Infrastructure.Data;
using TransferService.Infrastructure.Http;
using TransferService.Infrastructure.Messaging;
using TransferService.Infrastructure.Repositories;
using TransferService.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("ServiceName", "TransferService")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{ServiceName}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File("logs/transfer-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .WriteTo.Seq(builder.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341",
        apiKey: builder.Configuration["Seq:ApiKey"])
    .CreateLogger();

builder.Host.UseSerilog();

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<TransferDbContext>(opts =>
    opts.UseSqlServer(
        builder.Configuration.GetConnectionString("TransferDb"),
        sql => sql.EnableRetryOnFailure(maxRetryCount: 3)));

// ── Hangfire ──────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(
        builder.Configuration.GetConnectionString("TransferDb"),
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
    opts.WorkerCount = 2;
    opts.Queues      = new[] { "transfer", "default" };
});

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<ITransferRepository,    TransferRepository>();
builder.Services.AddScoped<IBeneficiaryRepository, BeneficiaryRepository>();
builder.Services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

// ── Application Services ───────────────────────────────────────────────────────
builder.Services.AddScoped<TransferApplicationService>();
builder.Services.AddScoped<TimeoutCheckJob>();

// ── Paystack HTTP Client ───────────────────────────────────────────────────────
builder.Services.AddHttpClient<IPaystackClient, PaystackClient>(client =>
{
    client.BaseAddress = new Uri("https://api.paystack.co/");
    client.Timeout     = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Authorization",
        $"Bearer {builder.Configuration["Paystack:SecretKey"]}");
})
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)), // 2s, 4s, 8s
        onRetry: (outcome, timespan, attempt, _) =>
            Log.Warning("Paystack API retry {Attempt}/3 after {Delay}s: {Reason}",
                attempt, timespan.TotalSeconds,
                outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString())))
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .CircuitBreakerAsync(
        handledEventsAllowedBeforeBreaking: 5,
        durationOfBreak: TimeSpan.FromSeconds(60),
        onBreak: (_, dur) => Log.Error("Paystack circuit breaker OPEN for {Dur}s", dur.TotalSeconds),
        onReset: () => Log.Information("Paystack circuit breaker CLOSED")));

// ── Wallet Service HTTP Client ────────────────────────────────────────────────
builder.Services.AddHttpClient<IWalletServiceClient, WalletServiceClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["WalletService:BaseUrl"] ?? "http://localhost:5003/");
    client.Timeout = TimeSpan.FromSeconds(10);
})
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(3,
        attempt => TimeSpan.FromMilliseconds(300 * attempt)))
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)));

// ── Kafka ─────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IKafkaPublisher, KafkaPublisher>();
builder.Services.AddHostedService<WalletMainTransferInitiatedConsumer>();

// ── Hangfire Job Registrar ─────────────────────────────────────────────────────
builder.Services.AddHostedService<HangfireJobRegistrar>();

// ── JWT Authentication ─────────────────────────────────────────────────────────
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

builder.Services.AddAuthorization();

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("TransferService"))
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
    .AddSqlServer(builder.Configuration.GetConnectionString("TransferDb")!)
    .AddKafka(new Confluent.Kafka.ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    })
    .AddUrlGroup(new Uri("https://api.paystack.co/"), name: "paystack-api", tags: new[] { "external" })
    .AddUrlGroup(
        new Uri(builder.Configuration["WalletService:BaseUrl"] + "health"),
        name: "wallet-service", tags: new[] { "dependency" });

// ── MVC + Swagger ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SplitSpend – Transfer Service", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization", Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer", BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {{
        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference
                { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
        }, Array.Empty<string>()
    }});
});

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TransferDbContext>();
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

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthFilter() }
});

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

await app.RegisterWithConsulAsync(app.Configuration);

app.Logger.LogInformation("TransferService starting on port {Port}",
    builder.Configuration["Service:Port"] ?? "5005");

app.Run();

public class HangfireAuthFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext ctx)
    {
        var http = ctx.GetHttpContext();
        return http.User.IsInRole("Admin") ||
               http.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
    }
}
