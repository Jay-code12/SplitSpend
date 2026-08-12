using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TransferService.Application.DTOs;
using TransferService.Application.Interfaces;
using TransferService.Application.Services;
using TransferService.Domain.Entities;

namespace TransferService.Controllers;

[ApiController]
[Route("api/transfers")]
public class TransferController : ControllerBase
{
    private readonly TransferApplicationService _svc;
    private readonly IPaystackClient _paystack;
    private readonly ILogger<TransferController> _log;

    public TransferController(
        TransferApplicationService svc,
        IPaystackClient paystack,
        ILogger<TransferController> log)
    {
        _svc      = svc;
        _paystack = paystack;
        _log      = log;
    }

    // POST /api/transfers/initiate
    /// <summary>
    /// Initiates an external bank transfer from Main Balance.
    /// PIN is validated by the API Gateway before this endpoint is reached.
    ///
    /// Steps triggered by this call:
    ///   1. Account name verified via Paystack
    ///   2. Main Balance checked against transfer amount
    ///   3. BankTransfer record created (Pending)
    ///   4. transfer.created emitted → Wallet pre-debits Main Balance
    ///   5. Wallet emits wallet.main.transfer.initiated → Paystack payout initiated
    /// </summary>
    [HttpPost("initiate")]
    [Authorize]
    [ProducesResponseType(typeof(InitiateTransferResponse), 202)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> Initiate(
        [FromBody] InitiateTransferRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _svc.InitiateAsync(req, ct);
            return Accepted(result); // 202 — processing is async
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (TransferDomainException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    // POST /api/transfers/webhook
    /// <summary>
    /// Receives Paystack transfer webhooks (transfer.success / transfer.failed / transfer.reversed).
    ///
    /// Security: HMAC-SHA512 signature verified FIRST before any payload is processed.
    /// No JWT required — Paystack doesn't send auth tokens.
    /// Gateway routes this with [Internal] tag per the MVP spec.
    ///
    /// Responds with 200 immediately — Paystack retries if it doesn't get 200 quickly.
    /// Processing is idempotent so duplicate webhook delivery is safe.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]  // Auth is via HMAC signature, not JWT
    [ProducesResponseType(typeof(WebhookAcknowledgement), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // Read raw body for HMAC verification — must be done before model binding
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawPayload = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        // Step 1: Verify signature — reject immediately if invalid
        var signature = Request.Headers["X-Paystack-Signature"].FirstOrDefault() ?? string.Empty;
        if (!_paystack.VerifyWebhookSignature(rawPayload, signature))
        {
            _log.LogWarning("Rejected webhook with invalid signature. IP={IP}",
                HttpContext.Connection.RemoteIpAddress);
            return BadRequest(new { message = "Invalid webhook signature." });
        }

        // Step 2: Deserialise
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
            return BadRequest(new { message = "Invalid webhook payload format." });
        }

        if (webhook == null)
            return BadRequest(new { message = "Empty webhook payload." });

        // Only handle transfer events — ignore charge.success etc.
        if (!webhook.Event.StartsWith("transfer.", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug("Non-transfer webhook ignored: {Event}", webhook.Event);
            return Ok(new WebhookAcknowledgement(false, "Event type not handled by this service."));
        }

        // Step 3: Process — respond 200 first to prevent Paystack retry timeout
        // Processing is idempotent so fire-and-forget is safe here
        _ = Task.Run(async () =>
        {
            try
            {
                await _svc.HandleWebhookAsync(webhook, rawPayload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Background webhook processing failed for event {Event}", webhook.Event);
            }
        }, ct);

        return Ok(new WebhookAcknowledgement(true, "Webhook received and queued for processing."));
    }

    // GET /api/transfers/{userId}
    /// <summary>Returns all transfers for a user, most recent first.</summary>
    [HttpGet("{userId:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(List<TransferDetailResponse>), 200)]
    public async Task<IActionResult> GetUserTransfers(Guid userId, CancellationToken ct)
    {
        var result = await _svc.GetUserTransfersAsync(userId, ct);
        return Ok(result);
    }

    // GET /api/transfers/{id}
    /// <summary>Returns a single transfer record with current status.</summary>
    [HttpGet("detail/{id:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(TransferDetailResponse), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetTransfer(
        Guid id, [FromQuery] Guid userId, CancellationToken ct)
    {
        try
        {
            var result = await _svc.GetTransferAsync(id, userId, ct);
            return Ok(result);
        }
        catch (TransferNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (TransferNotOwnedException)
        {
            return Forbid();
        }
    }

    // GET /api/transfers/banks
    /// <summary>
    /// Returns the list of supported Nigerian banks from Paystack.
    /// Response is cached by the API Gateway for 1 hour to avoid hammering Paystack.
    /// </summary>
    [HttpGet("banks")]
    [Authorize]
    [ProducesResponseType(typeof(BankListResponse), 200)]
    public async Task<IActionResult> GetBanks(CancellationToken ct)
    {
        var banks = await _paystack.GetBanksAsync(ct);
        return Ok(new BankListResponse(banks, DateTime.UtcNow));
    }

    // POST /api/transfers/verify-account
    /// <summary>
    /// Resolves an account name from account number + bank code via Paystack.
    /// Called by the frontend before the user confirms a transfer — gives them
    /// confirmation of who they're sending money to.
    /// </summary>
    [HttpPost("verify-account")]
    [Authorize]
    [ProducesResponseType(typeof(VerifyAccountResponse), 200)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> VerifyAccount(
        [FromBody] VerifyAccountRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _paystack.VerifyAccountAsync(req.AccountNumber, req.BankCode, ct);
            return Ok(result);
        }
        catch (PaystackApiException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    // GET /api/transfers/verify/{ref}
    /// <summary>
    /// Manually re-checks a transfer status against Paystack.
    /// Useful for recovery when a webhook was missed.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    [HttpGet("verify/{transferId:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(TransferDetailResponse), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> VerifyTransfer(
        Guid transferId, [FromQuery] Guid userId, CancellationToken ct)
    {
        try
        {
            var result = await _svc.VerifyTransferAsync(transferId, userId, ct);
            return Ok(result);
        }
        catch (TransferNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (TransferNotOwnedException)
        {
            return Forbid();
        }
    }
}
