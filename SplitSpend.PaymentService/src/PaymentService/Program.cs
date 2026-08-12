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
using PaymentService.Application.Interfaces;
using PaymentService.Application.Services;
using PaymentService.Infrastructure;
using PaymentService.Infrastructure.Data;
using PaymentService.Infrastructure.Http;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("ServiceName", "PaymentService")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{ServiceName}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File("logs/payment-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .WriteTo.Seq(builder.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341",
        apiKey: builder.Configuration["Seq:ApiKey"])
    .CreateLogger();

builder.Host.UseSerilog();

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<PaymentDbContext>(opts =>
    opts.UseSqlServer(
        builder.Configuration.GetConnectionString("PaymentDb"),
        sql => sql.EnableRetryOnFailure(maxRetryCount: 3)));

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<IPaymentLogRepository,     PaymentLogRepository>();
builder.Services.AddScoped<IVirtualAccountRepository, VirtualAccountRepository>();
builder.Services.AddScoped<IIdempotencyRepository,    IdempotencyRepository>();

// ── Application Service ────────────────────────────────────────────────────────
builder.Services.AddScoped<PaymentApplicationService>();

// ── Paystack HTTP Client (with Polly resilience) ───────────────────────────────
builder.Services.AddHttpClient<IPaystackClient, PaystackClient>(client =>
{
    client.BaseAddress = new Uri("https://api.paystack.co/");
    client.Timeout     = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add(
        "Authorization",
        $"Bearer {builder.Configuration["Paystack:SecretKey"]}");
})
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
        onRetry: (outcome, delay, attempt, _) =>
            Log.Warning(
                "Paystack API retry {Attempt}/3 after {Delay}s: {Reason}",
                attempt, delay.TotalSeconds,
                outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString())))
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .CircuitBreakerAsync(
        handledEventsAllowedBeforeBreaking: 5,
        durationOfBreak: TimeSpan.FromSeconds(60),
        onBreak: (_, dur) =>
            Log.Error("Paystack circuit breaker OPEN for {Dur}s", dur.TotalSeconds),
        onReset: () =>
            Log.Information("Paystack circuit breaker CLOSED")));

// ── Kafka Publisher ────────────────────────────────────────────────────────────
// Payment Service only PUBLISHES — it never consumes Kafka topics.
builder.Services.AddSingleton<IKafkaPublisher, KafkaPublisher>();

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
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("PaymentService"))
            .AddAspNetCoreInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(otlp =>
                otlp.Endpoint = new Uri(
                    builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317"));
    });

// ── Consul ─────────────────────────────────────────────────────────────────────
builder.Services.AddConsul(builder.Configuration);

// ── Health Checks ──────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddSqlServer(builder.Configuration.GetConnectionString("PaymentDb")!)
    .AddKafka(new Confluent.Kafka.ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    })
    .AddUrlGroup(
        new Uri("https://api.paystack.co/"),
        name: "paystack-api",
        tags: new[] { "external" });

// ── MVC + Swagger ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SplitSpend – Payment Service", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = Microsoft.OpenApi.Models.ParameterLocation.Header
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
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
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

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

await app.RegisterWithConsulAsync(app.Configuration);

app.Logger.LogInformation("PaymentService starting on port {Port}",
    builder.Configuration["Service:Port"] ?? "5007");

app.Run();
