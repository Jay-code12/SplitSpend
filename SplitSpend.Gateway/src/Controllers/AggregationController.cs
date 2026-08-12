using Microsoft.AspNetCore.Mvc;
using SplitSpend.Gateway.Aggregators;
using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Controllers;

/// <summary>
/// Exposes the two aggregated endpoints defined in the MVP documentation:
///   GET /api/dashboard/{userId}   — home screen data in one call
///   GET /api/vendor-pay/{id}/detail — payment approval screen data in one call
///
/// These are gateway-owned routes; they are NOT proxied by Ocelot. The gateway
/// assembles them by calling multiple downstream services in parallel.
/// </summary>
[ApiController]
public sealed class AggregationController(
    IDashboardAggregator      dashboardAggregator,
    IVendorPayDetailAggregator vendorPayDetailAggregator,
    ILogger<AggregationController> logger) : ControllerBase
{
    // ── GET /api/dashboard/{userId} ───────────────────────────────────────────
    [HttpGet("api/dashboard/{userId}")]
    public async Task<IActionResult> GetDashboard(
        string userId, CancellationToken ct)
    {
        var callerUserId  = HttpContext.Items["UserId"]?.ToString();
        var role          = HttpContext.Items["UserRole"]?.ToString();
        var correlationId = HttpContext.Items[GatewayHeaders.CorrelationId]?.ToString()
                            ?? "unknown";

        // Only the owner or an Admin may fetch the dashboard
        if (!string.Equals(callerUserId, userId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new GatewayErrorResponse
            {
                TraceId = correlationId,
                Status  = 403,
                Error   = "Forbidden",
                Message = "You may only retrieve your own dashboard."
            });
        }

        var bearerToken = ExtractRawBearerToken();

        logger.LogInformation(
            "Dashboard aggregation requested. UserId={UserId} CallerUserId={CallerUserId}",
            userId, callerUserId);

        var response = await dashboardAggregator.AggregateAsync(
            userId, bearerToken, correlationId, ct);

        return Ok(response);
    }

    // ── GET /api/vendor-pay/{id}/detail ──────────────────────────────────────
    [HttpGet("api/vendor-pay/{id}/detail")]
    public async Task<IActionResult> GetVendorPayDetail(string id, CancellationToken ct)
    {
        var buyerUserId   = HttpContext.Items["UserId"]?.ToString();
        var correlationId = HttpContext.Items[GatewayHeaders.CorrelationId]?.ToString()
                            ?? "unknown";
        var bearerToken   = ExtractRawBearerToken();

        logger.LogInformation(
            "VendorPayDetail aggregation requested. RequestId={RequestId} BuyerUserId={BuyerUserId}",
            id, buyerUserId);

        var response = await vendorPayDetailAggregator.AggregateAsync(
            id, buyerUserId, bearerToken, correlationId, ct);

        return Ok(response);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? ExtractRawBearerToken()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return header["Bearer ".Length..].Trim();
    }
}
