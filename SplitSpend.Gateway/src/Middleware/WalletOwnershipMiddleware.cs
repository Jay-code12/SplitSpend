using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Middleware;

/// <summary>
/// Enforces UserId ownership on all /api/wallets/{userId} routes.
/// Extracts the userId path segment and compares it to the authenticated
/// caller's UserId claim. Admin role bypasses the check.
///
/// This prevents a user from querying or modifying another user's wallet
/// purely at the gateway level — before the request ever reaches Wallet Service.
/// </summary>
public sealed class WalletOwnershipMiddleware(RequestDelegate next, ILogger<WalletOwnershipMiddleware> logger)
{
    private const string WalletPrefix = "/api/wallets/";

    public async Task InvokeAsync(HttpContext context)
    {
        var path   = context.Request.Path.Value ?? string.Empty;
        var userId = context.Items["UserId"]?.ToString();
        var role   = context.Items["UserRole"]?.ToString();

        // Only check GET /api/wallets/{userId}[/...] and POST-like calls
        if (RequiresOwnershipCheck(path, context.Request.Method))
        {
            // Admin bypasses ownership enforcement
            if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var routeUserId = ExtractUserIdFromPath(path);

            if (routeUserId is not null &&
                !string.Equals(routeUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Wallet ownership violation. CallerUserId={CallerUserId} RouteUserId={RouteUserId} Path={Path}",
                    userId ?? "unknown", routeUserId, path);

                var activity = System.Diagnostics.Activity.Current;
                activity?.SetTag("security.ownership_violation", true);

                await RespondForbidden(context,
                    "You are not authorised to access another user's wallet.");
                return;
            }
        }

        await next(context);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Ownership applies to paths like /api/wallets/{userId} and /api/wallets/{userId}/ledger
    // but NOT to shared endpoints like /api/wallets/credit or /api/wallets/debit
    private static bool RequiresOwnershipCheck(string path, string method)
    {
        if (!path.StartsWith(WalletPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var segment = path[WalletPrefix.Length..].Split('/')[0];

        // If the segment looks like a GUID it's a userId — check ownership
        return Guid.TryParse(segment, out _);
    }

    // Extract the first path segment after /api/wallets/
    private static string? ExtractUserIdFromPath(string path)
    {
        var afterPrefix = path[WalletPrefix.Length..];
        var slashIdx    = afterPrefix.IndexOf('/');
        return slashIdx >= 0
            ? afterPrefix[..slashIdx]
            : afterPrefix;
    }

    private static Task RespondForbidden(HttpContext context, string message)
    {
        context.Response.StatusCode  = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        var correlationId = context.Items[GatewayHeaders.CorrelationId]?.ToString() ?? "unknown";
        var body = System.Text.Json.JsonSerializer.Serialize(new GatewayErrorResponse
        {
            TraceId  = correlationId,
            Status   = 403,
            Error    = "Forbidden",
            Message  = message
        });
        return context.Response.WriteAsync(body);
    }
}
