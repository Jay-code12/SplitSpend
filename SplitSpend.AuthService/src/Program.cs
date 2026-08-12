using SplitSpend.AuthService.Extensions;
using SplitSpend.AuthService.Middleware.AuthMiddleware;
using SplitSpend.AuthService.Settings;

// ══════════════════════════════════════════════════════════════════════════════
// BUILDER
// ══════════════════════════════════════════════════════════════════════════════
var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json",                        optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables(); // Production secrets override appsettings

// ── Strongly-typed settings ────────────────────────────────────────────────────
var jwt     = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
              ?? throw new InvalidOperationException("Jwt settings are required.");
var consul  = builder.Configuration.GetSection("Consul").Get<ConsulSettings>()
              ?? throw new InvalidOperationException("Consul settings are required.");
var kafka   = builder.Configuration.GetSection("Kafka").Get<KafkaSettings>()
                ?? throw new InvalidOperationException("Kafka settings are required.");
var otSettings = builder.Configuration.GetSection("OpenTelemetry").Get<OpenTelemetrySettings>()
                ?? throw new InvalidOperationException("Kafka settings are required.");
var seq     = builder.Configuration.GetSection("Seq").Get<SeqSettings>()
              ?? new SeqSettings();

// Register settings as singletons for DI injection into services
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(consul);
builder.Services.AddSingleton(kafka);
builder.Services.AddSingleton(otSettings);
builder.Services.AddSingleton(seq);

// ── Structured logging (Serilog) — register first ─────────────────────────────
builder.AddAuthSerilog(seq);

// ── OpenTelemetry — traces ASP.NET Core + EF Core + HTTP + Kafka (MassTransit)
builder.Services.AddAuthOpenTelemetry(otSettings);

// ── Database + Repositories ────────────────────────────────────────────────────
builder.Services.AddDatabase(builder.Configuration);

// ── Application services + validators ─────────────────────────────────────────
builder.Services.AddApplicationServices();

// ── JWT Authentication ─────────────────────────────────────────────────────────
builder.Services.AddJwtAuthentication(jwt);

// ── Rate Limiting ──────────────────────────────────────────────────────────────
builder.Services.AddAuthRateLimiting();

// ── Kafka messaging (MassTransit) ─────────────────────────────────────────────
builder.Services.AddKafkaMessaging(kafka);

// ── Consul self-registration ───────────────────────────────────────────────────
builder.Services.AddConsulServiceDiscovery(consul);

// ── Health checks ──────────────────────────────────────────────────────────────
builder.Services.AddAuthHealthChecks(builder.Configuration);

// ── Controllers ────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ══════════════════════════════════════════════════════════════════════════════
// PIPELINE
// ══════════════════════════════════════════════════════════════════════════════
var app = builder.Build();

// ── 1. Auto-migrate DB on startup ─────────────────────────────────────────────
await app.MigrateDatabaseAsync();

// ── 2. Global exception handler ───────────────────────────────────────────────
app.UseMiddleware<GlobalExceptionMiddleware>();

// ── 3. Correlation ID ─────────────────────────────────────────────────────────
app.UseMiddleware<CorrelationIdMiddleware>();

// ── 4. Rate Limiting ──────────────────────────────────────────────────────────
app.UseRateLimiter();

// ── 5. Authentication + Authorization ─────────────────────────────────────────
app.UseAuthentication();
app.UseAuthorization();

// ── 6. Health checks ──────────────────────────────────────────────────────────
app.MapAuthHealthChecks();

// ── 7. Controllers ────────────────────────────────────────────────────────────
app.MapControllers();

app.Run();
