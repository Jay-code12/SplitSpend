using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Application.DTOs;
using PaymentService.Application.Interfaces;
using PaymentService.Application.Services;
using PaymentService.Domain.Entities;

namespace PaymentService.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentController : ControllerBase
{
    private readonly PaymentApplicationService _svc;
    private readonly IPaystackClient _paystack;
    private readonly ILogger<PaymentController> _log;

    public PaymentController(
        PaymentApplicationService svc,
        IPaystackClient paystack,
        ILogger<PaymentController> log)
    {
        _svc     = svc;
        _paystack = paystack;
        _log     = log;
    }

    // POST /api/payments/webhook
    /// <summary>
    /// Receives Paystack charge.success webhooks for virtual account deposits.
    ///
    /// Security model (from MVP spec):
    ///   - No JWT required — Paystack doesn't send auth tokens
    ///   - HMAC-SHA512 signature verified as the FIRST action — rejects before any processing
    ///   - Gateway routes this as [Internal] — not exposed to regular clients
    ///
    /// Reliability:
    ///   - Responds 200 immediately; Paystack retries if it doesn't get a fast 200
    ///   - Full processing is idempotent — duplicate webhook delivery is safe
    ///   - Raw body read before model binding to ensure exact bytes for HMAC
    ///
    /// MVP alert: payment.failed rate > 5% in 5 minutes → Critical alert in Application Insights
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(WebhookAcknowledgement), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // Step 1: Read raw body before model binding (required for HMAC verification)
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawPayload   = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        // Step 2: Verify HMAC-SHA512 signature — reject immediately if invalid
        var signature = Request.Headers["X-Paystack-Signature"].FirstOrDefault() ?? string.Empty;

        if (!_paystack.VerifyWebhookSignature(rawPayload, signature))
        {
            _log.LogWarning(
                "Webhook rejected: invalid signature. IP={IP} UserAgent={UA}",
                HttpContext.Connection.RemoteIpAddress,
                Request.Headers.UserAgent.FirstOrDefault());

            return BadRequest(new { message = "Invalid webhook signature." });
        }

        // Step 3: Deserialise
        PaystackWebhookRequest? webhook;
        try
        {
            webhook = JsonSerializer.Deserialize<PaystackWebhookRequest>(
                rawPayload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Failed to deserialise Paystack webhook payload");
            return BadRequest(new { message = "Invalid payload format." });
        }

        if (webhook == null)
            return BadRequest(new { message = "Empty payload." });

        // Step 4: Only handle charge.success — log and ignore everything else
        if (!string.Equals(webhook.Event, "charge.success", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug("Non-deposit webhook event ignored: {Event}", webhook.Event);
            return Ok(new WebhookAcknowledgement(false, $"Event '{webhook.Event}' not handled by Payment Service."));
        }

        // Step 5: Respond 200 to Paystack immediately, process async
        // Processing is idempotent so fire-and-forget is safe
        _ = Task.Run(async () =>
        {
            try
            {
                await _svc.HandleDepositWebhookAsync(webhook, rawPayload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Async webhook processing failed for ref={Ref}",
                    webhook.Data?.Reference ?? "unknown");
            }
        }, ct);

        return Ok(new WebhookAcknowledgement(true, "Webhook received."));
    }

    // GET /api/payments/verify/{ref}
    /// <summary>
    /// Manually re-verifies a deposit against Paystack and processes it if missed.
    ///
    /// Use case: user's money left their bank but SplitSpend balance didn't update.
    /// This endpoint can be triggered by support or the user directly as recovery.
    /// It is fully idempotent — already-processed references return the existing result.
    /// </summary>
    [HttpGet("verify/{reference}")]
    [Authorize]
    [ProducesResponseType(typeof(ManualVerifyResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ManualVerify(string reference, CancellationToken ct)
    {
        try
        {
            var result = await _svc.VerifyAndProcessAsync(reference, ct);
            return Ok(result);
        }
        catch (UserResolutionException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual verify failed for ref={Ref}", reference);
            return StatusCode(500, new { message = "Could not verify payment at this time." });
        }
    }

    // GET /api/payments/{userId}/history
    /// <summary>
    /// Returns the deposit history for a user, most recent first.
    /// Only shows deposits — no vendor payments or transfers (those are in Transaction Service).
    /// </summary>
    [HttpGet("{userId:guid}/history")]
    [Authorize]
    [ProducesResponseType(typeof(List<PaymentLogResponse>), 200)]
    public async Task<IActionResult> GetHistory(Guid userId, CancellationToken ct)
    {
        var result = await _svc.GetPaymentHistoryAsync(userId, ct);
        return Ok(result);
    }

    // POST /api/payments/virtual-account
    /// <summary>
    /// Provisions a Paystack dedicated virtual account for a new user.
    /// Called during registration flow — each user gets a unique Nigerian bank account.
    /// Idempotent: returns existing account if already provisioned.
    /// </summary>
    [HttpPost("virtual-account")]
    [Authorize]
    [ProducesResponseType(typeof(VirtualAccountResponse), 201)]
    [ProducesResponseType(200)]
    public async Task<IActionResult> ProvisionVirtualAccount(
        [FromBody] ProvisionVirtualAccountRequest req, CancellationToken ct)
    {
        var result = await _svc.ProvisionVirtualAccountAsync(req, ct);

        // 200 if already existed, 201 if newly created
        var existing = await _svc.GetVirtualAccountAsync(req.UserId, ct);
        return existing.CreatedAt < DateTime.UtcNow.AddSeconds(-2)
            ? Ok(result)
            : StatusCode(201, result);
    }

    // GET /api/payments/{userId}/virtual-account
    /// <summary>
    /// Returns a user's virtual account details so they know where to send money.
    /// </summary>
    [HttpGet("{userId:guid}/virtual-account")]
    [Authorize]
    [ProducesResponseType(typeof(VirtualAccountResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetVirtualAccount(Guid userId, CancellationToken ct)
    {
        try
        {
            var result = await _svc.GetVirtualAccountAsync(userId, ct);
            return Ok(result);
        }
        catch (VirtualAccountNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
