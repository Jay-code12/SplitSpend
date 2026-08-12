using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WalletService.Application.DTOs;
using WalletService.Application.Services;
using WalletService.Domain.Entities;

namespace WalletService.Controllers;

[ApiController]
[Route("api/wallets")]
[Authorize]
public class WalletController : ControllerBase
{
    private readonly WalletApplicationService _svc;
    private readonly ILogger<WalletController> _log;

    public WalletController(WalletApplicationService svc, ILogger<WalletController> log)
    {
        _svc = svc;
        _log = log;
    }

    // GET /api/wallets/{userId}
    /// <summary>Returns the current MainBalance and BudgetBalance for a user.</summary>
    [HttpGet("{userId:guid}")]
    [ProducesResponseType(typeof(WalletBalanceResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetBalance(Guid userId, CancellationToken ct)
    {
        try
        {
            var result = await _svc.GetBalanceAsync(userId, ct);
            return Ok(result);
        }
        catch (WalletNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // POST /api/wallets/credit
    /// <summary>
    /// Credits a wallet balance (Main or Budget).
    /// Used by Payment Service (deposit) and internal operations.
    /// Idempotency enforced via IdempotencyKey header or body field.
    /// </summary>
    [HttpPost("credit")]
    [ProducesResponseType(typeof(CreditResponse), 200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Credit([FromBody] CreditRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _svc.CreditAsync(req, ct);
            return Ok(result);
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WalletNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // POST /api/wallets/debit
    /// <summary>
    /// Debits with budget-first logic.
    /// Emits wallet.budget.debited and/or wallet.main.debited based on which balance was used.
    /// Emits wallet.insufficient_funds if neither balance can cover the amount.
    /// </summary>
    [HttpPost("debit")]
    [ProducesResponseType(typeof(DebitResponse), 200)]
    [ProducesResponseType(409)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> Debit([FromBody] DebitRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _svc.DebitAsync(req, ct);
            return Ok(result);
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InsufficientFundsException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (WalletSuspendedException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (WalletNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // POST /api/wallets/pay
    /// <summary>
    /// Atomic in-platform payment: debits payer (budget-first) and credits recipient Main Balance
    /// in a single DB transaction. No Paystack. Instant settlement.
    /// Used by Vendor Pay Service after vendor.payment.approved — but also exposed as REST
    /// for Gateway-initiated flows.
    /// </summary>
    [HttpPost("pay")]
    [ProducesResponseType(typeof(InPlatformPayResponse), 200)]
    [ProducesResponseType(409)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> Pay([FromBody] InPlatformPayRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _svc.PayAsync(req, ct);
            return Ok(result);
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InsufficientFundsException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (WalletNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // POST /api/wallets/internal-transfer
    /// <summary>
    /// Moves funds between Main and Budget balances (no external money movement).
    /// Direction: "MainToBudget" for budget creation; "BudgetToMain" for expiry or cancellation.
    /// </summary>
    [HttpPost("internal-transfer")]
    [ProducesResponseType(typeof(InternalTransferResponse), 200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> InternalTransfer(
        [FromBody] InternalTransferRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _svc.InternalTransferAsync(req, ct);
            return Ok(result);
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WalletNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // GET /api/wallets/{userId}/ledger
    /// <summary>
    /// Paginated ledger history for a user, filterable by type, date range.
    /// Uses cursor-based pagination for stable, efficient paging.
    /// </summary>
    [HttpGet("{userId:guid}/ledger")]
    [ProducesResponseType(typeof(PagedLedgerResponse), 200)]
    public async Task<IActionResult> GetLedger(
        Guid userId,
        [FromQuery] string? type,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? cursor,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _svc.GetLedgerAsync(
            new LedgerQueryRequest(userId, type, from, to, cursor, Math.Clamp(pageSize, 1, 100)), ct);
        return Ok(result);
    }
}
