using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Ocelot.Provider.Consul;
using Ocelot.Provider.Polly;
using SplitSpend.Gateway.Aggregators;
using SplitSpend.Gateway.Configuration;
using SplitSpend.Gateway.Extensions;
using SplitSpend.Gateway.Middleware;

// ══════════════════════════════════════════════════════════════════════════════
// BUILDER
// ══════════════════════════════════════════════════════════════════════════════
var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json",                        optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddJsonFile("ocelot.json",                             optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();  // Secrets override appsettings in production

// ── Strongly-typed settings ───────────────────────────────────────────────────
var jwtSettings  = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
                   ?? throw new InvalidOperationException("Jwt settings are required.");
var consulSettings = builder.Configuration.GetSection("Consul").Get<ConsulSettings>()
                   ?? throw new InvalidOperationException("Consul settings are required.");
var otSettings   = builder.Configuration.GetSection("OpenTelemetry").Get<OpenTelemetrySettings>()
                   ?? new OpenTelemetrySettings();
var seqSettings  = builder.Configuration.GetSection("Seq").Get<SeqSettings>()
                   ?? new SeqSettings();
var rlSettings   = builder.Configuration.GetSection("RateLimiting").Get<RateLimitingSettings>()
                   ?? new RateLimitingSettings();
var resSettings  = builder.Configuration.GetSection("Resilience").Get<ResilienceSettings>()
                   ?? new ResilienceSettings();

// Register settings as singletons so middleware/services can inject them
builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton(consulSettings);
builder.Services.AddSingleton(otSettings);
builder.Services.AddSingleton(seqSettings);
builder.Services.AddSingleton(rlSettings);
builder.Services.AddSingleton(resSettings);

// ── Structured Logging (Serilog) ──────────────────────────────────────────────
// Must be registered early so every subsequent log call uses Serilog.
builder.AddSplitSpendSerilog(seqSettings);

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
// Instruments ASP.NET Core + HttpClient and exports to OTLP + Azure Monitor.
// W3C TraceContext headers are automatically propagated on every outbound call.
builder.Services.AddSplitSpendOpenTelemetry(otSettings);

// ── Consul service discovery ──────────────────────────────────────────────────
builder.Services.AddSplitSpendConsul(consulSettings);

// ── JWT Authentication ────────────────────────────────────────────────────────
// Required by Ocelot's AuthenticationOptions.AuthenticationProviderKey = "Bearer".
// Our custom JwtAuthMiddleware does the actual per-request validation earlier in
// the pipeline; this registration satisfies Ocelot's internal requirements.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer("Bearer", opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtSettings.Issuer,
            ValidAudience            = jwtSettings.Audience,
            IssuerSigningKey         = new SymmetricSecurityKey(
                                           Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            ClockSkew                = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// ── Rate Limiting ─────────────────────────────────────────────────────────────
builder.Services.AddSplitSpendRateLimiting(rlSettings);

// ── Resilient HttpClient for aggregators ─────────────────────────────────────
builder.Services.AddSplitSpendHttpClients(resSettings);

// ── Aggregators ───────────────────────────────────────────────────────────────
builder.Services.AddScoped<IDashboardAggregator,       DashboardAggregator>();
builder.Services.AddScoped<IVendorPayDetailAggregator, VendorPayDetailAggregator>();

// ── Controllers (aggregation endpoints) ──────────────────────────────────────
builder.Services.AddControllers();

// ── Health Checks ─────────────────────────────────────────────────────────────
builder.Services.AddSplitSpendHealthChecks();

// ── Response Caching (for aggregated endpoints) ───────────────────────────────
builder.Services.AddResponseCaching();
builder.Services.AddMemoryCache();

// ── Ocelot ───────────────────────────────────────────────────────────────────
// AddConsul()  — enables Consul as the service discovery provider
// AddPolly()   — enables QoS (circuit breaker + timeout) per route
builder.Services
    .AddOcelot(builder.Configuration)
    .AddConsul()
    .AddPolly();

// ══════════════════════════════════════════════════════════════════════════════
// PIPELINE
// ══════════════════════════════════════════════════════════════════════════════
var app = builder.Build();

// ── 1. Global exception handler (outermost — catches everything below) ────────
app.UseMiddleware<GlobalExceptionMiddleware>();

// ── 2. Request timing ─────────────────────────────────────────────────────────
app.UseMiddleware<RequestTimingMiddleware>();

// ── 3. Correlation ID — must run before any logging or auth ──────────────────
app.UseMiddleware<CorrelationIdMiddleware>();

// ── 4. Rate Limiting ──────────────────────────────────────────────────────────
// Route-aware policies are applied via endpoint metadata on controllers;
// Ocelot-proxied routes use Ocelot's built-in rate limiting (ocelot.json).
app.UseRateLimiter();

// ── 5. Authentication & Authorisation ────────────────────────────────────────
app.UseAuthentication();
app.UseAuthorization();

// ── 6. Custom JWT extraction + enrichment ────────────────────────────────────
// Runs after UseAuthentication so the ClaimsPrincipal is already populated.
// Extracts UserId/Role and stamps downstream headers.
app.UseMiddleware<JwtAuthMiddleware>();

// ── 7. PIN Guard (transfer routes only) ──────────────────────────────────────
app.UseMiddleware<PinGuardMiddleware>();

// ── 8. Wallet Ownership enforcement ──────────────────────────────────────────
app.UseMiddleware<WalletOwnershipMiddleware>();

// ── 9. Health checks (before Ocelot so they are never proxied) ───────────────
app.MapSplitSpendHealthChecks();

// ── 10. Aggregation controllers (gateway-owned routes, not proxied) ───────────
app.MapControllers();

// ── 11. Ocelot (proxy everything else to downstream services via Consul) ──────
// Must be last — Ocelot catches all unmatched routes and proxies them.
await app.UseOcelot();

app.Run();
