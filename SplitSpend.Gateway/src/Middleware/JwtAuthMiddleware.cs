using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using SplitSpend.Gateway.Configuration;
using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Middleware;

/// <summary>
/// Validates JWT Bearer tokens on every authenticated route.
/// Extracts UserId and Role, stamps X-User-Id / X-User-Role on the downstream
/// request so each microservice trusts the gateway's authentication decision.
/// Unauthenticated routes (auth/*, webhooks) pass through without a token.
/// </summary>
public sealed class JwtAuthMiddleware(
    RequestDelegate      next,
    JwtSettings          jwtSettings,
    ILogger<JwtAuthMiddleware> logger)
{
    // Routes that do NOT require authentication
    private static readonly HashSet<string> PublicPrefixes =
    [
        "/api/auth",
        "/api/payments/webhook",
        "/api/transfers/webhook",
        "/health"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (IsPublicRoute(path))
        {
            await next(context);
            return;
        }

        var token = ExtractBearerToken(context);

        if (string.IsNullOrWhiteSpace(token))
        {
            await RespondUnauthorized(context, "Missing or malformed Authorization header.");
            return;
        }

        var (principal, error) = ValidateToken(token);

        if (principal is null)
        {
            logger.LogWarning("JWT validation failed. Path={Path} Error={Error}", path, error);
            await RespondUnauthorized(context, error ?? "Invalid token.");
            return;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? principal.FindFirstValue("sub");
        var role   = principal.FindFirstValue(ClaimTypes.Role)
                     ?? principal.FindFirstValue("role");

        if (string.IsNullOrWhiteSpace(userId))
        {
            await RespondUnauthorized(context, "Token does not contain a valid user identity.");
            return;
        }

        // Stamp downstream headers — microservices trust these
        context.Request.Headers[GatewayHeaders.UserId]   = userId;
        context.Request.Headers[GatewayHeaders.UserRole]  = role ?? "User";

        // Store in Items for other middleware
        context.Items["UserId"]   = userId;
        context.Items["UserRole"] = role ?? "User";

        // Enrich log context
        using (Serilog.Context.LogContext.PushProperty("UserId",   userId))
        using (Serilog.Context.LogContext.PushProperty("UserRole", role ?? "User"))
        {
            // Enrich the current OpenTelemetry span
            var activity = System.Diagnostics.Activity.Current;
            activity?.SetTag("user.id",   userId);
            activity?.SetTag("user.role", role ?? "User");

            await next(context);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsPublicRoute(string path) =>
        PublicPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractBearerToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader is null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return authHeader["Bearer ".Length..].Trim();
    }

    private (ClaimsPrincipal? principal, string? error) ValidateToken(string token)
    {
        var handler    = new JwtSecurityTokenHandler();
        var key        = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey));
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtSettings.Issuer,
            ValidAudience            = jwtSettings.Audience,
            IssuerSigningKey         = key,
            ClockSkew                = TimeSpan.FromSeconds(30)
        };

        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);
            return (principal, null);
        }
        catch (SecurityTokenExpiredException)
        {
            return (null, "Token has expired.");
        }
        catch (SecurityTokenException ex)
        {
            return (null, ex.Message);
        }
    }

    private static Task RespondUnauthorized(HttpContext context, string message)
    {
        context.Response.StatusCode  = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        var correlationId = context.Items[GatewayHeaders.CorrelationId]?.ToString() ?? "unknown";
        var body = System.Text.Json.JsonSerializer.Serialize(new GatewayErrorResponse
        {
            TraceId  = correlationId,
            Status   = 401,
            Error    = "Unauthorized",
            Message  = message
        });
        return context.Response.WriteAsync(body);
    }
}
