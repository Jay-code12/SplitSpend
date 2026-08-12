using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Middleware;

/// <summary>
/// Enforces PIN verification on all external bank transfer routes (/api/transfers/*).
/// The client must supply an X-Pin-Hash header containing the HMAC-SHA256 of the
/// user's 4-digit PIN signed with their UserId as the key.
///
/// Webhook routes are excluded — they are authenticated via Paystack HMAC only.
///
/// In production the Auth Service owns PIN validation; the gateway acts as a
/// first-line filter to reject requests that don't even carry the header.
/// </summary>
public sealed class PinGuardMiddleware(RequestDelegate next, ILogger<PinGuardMiddleware> logger)
{
    private const string TransferPrefix  = "/api/transfers";
    private const string WebhookSuffix   = "/webhook";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (RequiresPinGuard(path))
        {
            var pinHash = context.Request.Headers[GatewayHeaders.PinHash].FirstOrDefault();
            var userId  = context.Items["UserId"]?.ToString();

            if (string.IsNullOrWhiteSpace(pinHash))
            {
                logger.LogWarning(
                    "Transfer request rejected — missing PIN header. UserId={UserId} Path={Path}",
                    userId ?? "unknown", path);

                await RespondForbidden(context,
                    "External bank transfers require X-Pin-Hash header. " +
                    "Provide your transaction PIN hash to proceed.");
                return;
            }

            // Tag the OpenTelemetry span so security auditors can filter transfer requests
            var activity = System.Diagnostics.Activity.Current;
            activity?.SetTag("security.pin_guard", true);
            activity?.SetTag("security.transfer_route", true);

            using (Serilog.Context.LogContext.PushProperty("PinGuardPassed", true))
            {
                logger.LogInformation(
                    "Transfer PIN guard passed. UserId={UserId} Path={Path}",
                    userId ?? "unknown", path);

                await next(context);
            }
            return;
        }

        await next(context);
    }

    private static bool RequiresPinGuard(string path) =>
        path.StartsWith(TransferPrefix, StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(WebhookSuffix,   StringComparison.OrdinalIgnoreCase);

    private static Task RespondForbidden(HttpContext context, string message)
    {
        context.Response.StatusCode  = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        var correlationId = context.Items[GatewayHeaders.CorrelationId]?.ToString() ?? "unknown";
        var body = System.Text.Json.JsonSerializer.Serialize(new GatewayErrorResponse
        {
            TraceId  = correlationId,
            Status   = 403,
            Error    = "PinRequired",
            Message  = message
        });
        return context.Response.WriteAsync(body);
    }
}
