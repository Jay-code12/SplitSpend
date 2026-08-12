using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using System.Text;
using TransactionService.Application.Interfaces;
using TransactionService.Application.Services;
using TransactionService.Consumers;
using TransactionService.Infrastructure.Data;
using TransactionService.Infrastructure.Messaging;
using TransactionService.Infrastructure.Repositories;
using TransactionService.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("ServiceName", "TransactionService")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{ServiceName}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File("logs/transaction-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .WriteTo.Seq(builder.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341",
        apiKey: builder.Configuration["Seq:ApiKey"])
    .CreateLogger();

builder.Host.UseSerilog();

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<TransactionDbContext>(opts =>
    opts.UseSqlServer(
        builder.Configuration.GetConnectionString("TransactionDb"),
        sql => sql.EnableRetryOnFailure(maxRetryCount: 3)));

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

// ── Application Services ───────────────────────────────────────────────────────
builder.Services.AddScoped<TransactionApplicationService>();

// ── Kafka Publisher ────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IKafkaPublisher, KafkaPublisher>();

// ── Kafka Consumers (10 background services) ───────────────────────────────────
builder.Services.AddHostedService<VendorPaymentApprovedConsumer>();
builder.Services.AddHostedService<WalletBudgetDebitedConsumer>();
builder.Services.AddHostedService<WalletMainDebitedConsumer>();
builder.Services.AddHostedService<WalletCreditedConsumer>();
builder.Services.AddHostedService<WalletInsufficientFundsConsumer>();
builder.Services.AddHostedService<PaymentSuccessfulConsumer>();
builder.Services.AddHostedService<PaymentFailedConsumer>();
builder.Services.AddHostedService<TransferCreatedConsumer>();
builder.Services.AddHostedService<TransferCompletedConsumer>();
builder.Services.AddHostedService<TransferFailedConsumer>();

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
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("TransactionService"))
            .AddAspNetCoreInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter(otlp =>
                otlp.Endpoint = new Uri(
                    builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317"));
    });

// ── Consul ─────────────────────────────────────────────────────────────────────
builder.Services.AddConsul(builder.Configuration);

// ── Health Checks ──────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddSqlServer(builder.Configuration.GetConnectionString("TransactionDb")!)
    .AddKafka(new Confluent.Kafka.ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    });

// ── MVC + Swagger ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SplitSpend – Transaction Service", Version = "v1" });
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
    var db = scope.ServiceProvider.GetRequiredService<TransactionDbContext>();
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

app.Logger.LogInformation("TransactionService starting on port {Port}",
    builder.Configuration["Service:Port"] ?? "5006");

app.Run();
