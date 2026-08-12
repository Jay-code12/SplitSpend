using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SplitSpend.Gateway.Configuration;
using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Extensions;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers all six rate limit policies from the MVP documentation using
    /// ASP.NET Core 8's built-in System.Threading.RateLimiting infrastructure.
    ///
    /// Policies (all fixed-window):
    ///   global-ip          — 100 req / 60 s  per IP  (unauthenticated)
    ///   authenticated-user — 300 req / 60 s  per UserId
    ///   auth-endpoint      —   5 req / 60 s  per IP  (/api/auth/login brute-force guard)
    ///   payment-endpoint   —  10 req / 60 s  per UserId
    ///   transfer-endpoint  —   5 req / 60 s  per UserId (tightest — money movement)
    ///   vendor-pay-endpoint — 20 req / 60 s  per UserId/VendorId
    ///
    /// The partition key is the X-User-Id header for authenticated policies,
    /// or the caller IP for public policies — never a cookie or session.
    /// </summary>
    public static IServiceCollection AddSplitSpendRateLimiting(
        this IServiceCollection  services,
        RateLimitingSettings     settings)
    {
        services.AddRateLimiter(limiter =>
        {
            // ── 1. Global IP (unauthenticated catch-all) ───────────────────
            limiter.AddPolicy(
                RateLimitPolicies.GlobalIp,
                ctx => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit         = settings.GlobalIpLimit.PermitLimit,
                        Window              = TimeSpan.FromSeconds(settings.GlobalIpLimit.WindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit          = 0
                    }));

            // ── 2. Authenticated user (general) ───────────────────────────
            limiter.AddPolicy(
                RateLimitPolicies.AuthenticatedUser,
                ctx => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: UserId(ctx),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.AuthenticatedUserLimit.PermitLimit,
                        Window      = TimeSpan.FromSeconds(settings.AuthenticatedUserLimit.WindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            // ── 3. Auth endpoints (brute-force protection) ────────────────
            limiter.AddPolicy(
                RateLimitPolicies.AuthEndpoint,
                ctx => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.AuthEndpointLimit.PermitLimit,
                        Window      = TimeSpan.FromSeconds(settings.AuthEndpointLimit.WindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            // ── 4. Payment endpoints ──────────────────────────────────────
            limiter.AddPolicy(
                RateLimitPolicies.PaymentEndpoint,
                ctx => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: UserId(ctx),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PaymentEndpointLimit.PermitLimit,
                        Window      = TimeSpan.FromSeconds(settings.PaymentEndpointLimit.WindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            // ── 5. Transfer endpoints (tightest — external money movement) ─
            limiter.AddPolicy(
                RateLimitPolicies.TransferEndpoint,
                ctx => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: UserId(ctx),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.TransferEndpointLimit.PermitLimit,
                        Window      = TimeSpan.FromSeconds(settings.TransferEndpointLimit.WindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            // ── 6. Vendor Pay endpoints ───────────────────────────────────
            limiter.AddPolicy(
                RateLimitPolicies.VendorPayEndpoint,
                ctx => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: UserId(ctx),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.VendorPayEndpointLimit.PermitLimit,
                        Window      = TimeSpan.FromSeconds(settings.VendorPayEndpointLimit.WindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit  = 0
                    }));

            // ── Rejection handler — returns 429 with retry-after header ───
            limiter.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.StatusCode  = StatusCodes.Status429TooManyRequests;
                ctx.HttpContext.Response.ContentType = "application/json";

                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    ctx.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();

                var correlationId =
                    ctx.HttpContext.Items[GatewayHeaders.CorrelationId]?.ToString() ?? "unknown";

                await ctx.HttpContext.Response.WriteAsJsonAsync(new GatewayErrorResponse
                {
                    TraceId = correlationId,
                    Status  = 429,
                    Error   = "TooManyRequests",
                    Message = "You have exceeded the rate limit for this endpoint. Please slow down."
                }, ct);
            };
        });

        return services;
    }

    // Extract the UserId header stamped by JwtAuthMiddleware, fall back to IP
    private static string UserId(HttpContext ctx) =>
        ctx.Request.Headers[GatewayHeaders.UserId].FirstOrDefault()
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
}
