using SplitSpend.UserService.Common;
using SplitSpend.UserService.Extensions;
using SplitSpend.UserService.Middleware;

// ══════════════════════════════════════════════════════════════════════════════
// BUILDER
// ══════════════════════════════════════════════════════════════════════════════
var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json",                        optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

// ── Strongly-typed settings ────────────────────────────────────────────────────
var jwt     = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
              ?? throw new InvalidOperationException("Jwt settings are required.");
var consul  = builder.Configuration.GetSection("Consul").Get<ConsulSettings>()
              ?? throw new InvalidOperationException("Consul settings are required.");
var kafka   = builder.Configuration.GetSection("Kafka").Get<KafkaSettings>()
              ?? new KafkaSettings();
var ot      = builder.Configuration.GetSection("OpenTelemetry").Get<OpenTelemetrySettings>()
              ?? new OpenTelemetrySettings();
var seq     = builder.Configuration.GetSection("Seq").Get<SeqSettings>()
              ?? new SeqSettings();

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(consul);
builder.Services.AddSingleton(kafka);
builder.Services.AddSingleton(ot);
builder.Services.AddSingleton(seq);

// ── Logging ────────────────────────────────────────────────────────────────────
builder.AddUserSerilog(seq);

// ── Observability ──────────────────────────────────────────────────────────────
builder.Services.AddUserOpenTelemetry(ot);

// ── Database ───────────────────────────────────────────────────────────────────
builder.Services.AddDatabase(builder.Configuration);

// ── Application ────────────────────────────────────────────────────────────────
builder.Services.AddApplicationServices();

// ── Auth ───────────────────────────────────────────────────────────────────────
builder.Services.AddJwtAuthentication(jwt);

// ── Rate Limiting ──────────────────────────────────────────────────────────────
builder.Services.AddUserRateLimiting();

// ── Kafka ──────────────────────────────────────────────────────────────────────
builder.Services.AddKafkaMessaging(kafka);

// ── Consul ─────────────────────────────────────────────────────────────────────
builder.Services.AddConsulServiceDiscovery(consul);

// ── Health Checks ──────────────────────────────────────────────────────────────
builder.Services.AddUserHealthChecks(builder.Configuration);

// ── Controllers ────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ══════════════════════════════════════════════════════════════════════════════
// PIPELINE
// ══════════════════════════════════════════════════════════════════════════════
var app = builder.Build();

await app.MigrateDatabaseAsync();

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapUserHealthChecks();
app.MapControllers();

app.Run();
