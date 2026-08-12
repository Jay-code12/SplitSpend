using BudgetService.Application.DTOs;
using BudgetService.Application.Services;
using BudgetService.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BudgetService.Controllers;

[ApiController]
[Route("api/budgets")]
[Authorize]
public class BudgetController : ControllerBase
{
    private readonly BudgetApplicationService _svc;
    private readonly DailyCronService _cron;
    private readonly ILogger<BudgetController> _log;

    public BudgetController(
        BudgetApplicationService svc,
        DailyCronService cron,
        ILogger<BudgetController> log)
    {
        _svc  = svc;
        _cron = cron;
        _log  = log;
    }

    // POST /api/budgets/{userId}
    /// <summary>
    /// Create a new budget plan.
    /// 1. Validates idempotency key
    /// 2. Sync REST call to Wallet Service — verifies MainBalance >= totalAmount
    /// 3. Creates Budget (Pending) and emits budget.created
    /// 4. Wallet Service handles the actual Main → Budget fund transfer
    /// 5. Budget activates when wallet.budget.transfer.completed arrives
    /// </summary>
    [HttpPost("{userId:guid}")]
    [ProducesResponseType(typeof(BudgetResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> CreateBudget(
        Guid userId,
        [FromBody] CreateBudgetRequest req,
        CancellationToken ct)
    {
        if (userId != req.UserId)
            return BadRequest(new { message = "UserId in route must match body." });

        try
        {
            var result = await _svc.CreateBudgetAsync(req, ct);
            return CreatedAtAction(nameof(GetDailySummary),
                new { userId = result.UserId }, result);
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InsufficientWalletBalanceException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (BudgetDomainException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // GET /api/budgets/{userId}/daily
    /// <summary>
    /// Returns today's total allocation, total spent, and remaining across all active budgets.
    /// Also returns a per-budget breakdown for the dashboard.
    /// </summary>
    [HttpGet("{userId:guid}/daily")]
    [ProducesResponseType(typeof(DailySummaryResponse), 200)]
    public async Task<IActionResult> GetDailySummary(Guid userId, CancellationToken ct)
    {
        var result = await _svc.GetDailySummaryAsync(userId, ct);
        return Ok(result);
    }

    // GET /api/budgets/{id}
    /// <summary>Returns a single budget by ID. User must own the budget.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BudgetResponse), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetBudget(Guid id, [FromQuery] Guid userId, CancellationToken ct)
    {
        try
        {
            var result = await _svc.GetBudgetAsync(id, userId, ct);
            return Ok(result);
        }
        catch (BudgetNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (BudgetNotOwnedException ex)
        {
            return Forbid();
        }
    }

    // POST /api/budgets/{id}/cancel
    /// <summary>
    /// Cancel an active budget. Remaining funds are returned to Main Balance via
    /// budget.cancelled → Wallet Service.
    /// Gift budgets cannot be cancelled.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(BudgetResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CancelBudget(
        Guid id,
        [FromBody] CancelBudgetRequest req,
        CancellationToken ct)
    {
        if (id != req.BudgetId)
            return BadRequest(new { message = "BudgetId in route must match body." });

        try
        {
            var result = await _svc.CancelBudgetAsync(req, ct);
            return Ok(result);
        }
        catch (BudgetNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (BudgetNotOwnedException)
        {
            return Forbid();
        }
        catch (BudgetDomainException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // POST /api/budgets/gift
    /// <summary>
    /// Send a gift budget to another SplitSpend user.
    /// 1. Verifies sender has enough Main Balance
    /// 2. Creates GiftBudget record and emits gift.sent
    /// 3. Wallet Service debits sender Main and credits receiver Budget
    /// 4. Budget Service creates a Budget for the receiver after wallet confirms
    /// Gift budgets cannot be cancelled by the receiver.
    /// </summary>
    [HttpPost("gift")]
    [ProducesResponseType(typeof(GiftBudgetResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> SendGift(
        [FromBody] SendGiftRequest req,
        CancellationToken ct)
    {
        try
        {
            var result = await _svc.SendGiftAsync(req, ct);
            return StatusCode(201, result);
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InsufficientWalletBalanceException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (BudgetDomainException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // GET /api/budgets/daily-end-start
    /// <summary>
    /// Manually triggers the end-of-day expiry + start-of-day release cycle.
    /// Called by the Hangfire CRON job — also exposed here for admin-triggered recovery
    /// if the CRON job fails to fire (see MVP alert: "Budget CRON job did not fire by 06:10 AM").
    /// Requires Admin role.
    /// </summary>
    [HttpGet("daily-end-start")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> TriggerDailyEndStart(CancellationToken ct)
    {
        _log.LogWarning("Manual CRON trigger via API — initiated by admin");

        await _cron.RunDailyExpiryAsync(ct);
        await _cron.RunDailyReleaseAsync(ct);

        return Ok(new { message = "Daily expiry and release jobs triggered successfully." });
    }
}
