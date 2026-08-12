using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TransactionService.Application.DTOs;
using TransactionService.Application.Services;
using TransactionService.Domain.Entities;

namespace TransactionService.Controllers;

[ApiController]
[Route("api/transactions")]
[Authorize]
public class TransactionController : ControllerBase
{
    private readonly TransactionApplicationService _svc;
    private readonly ILogger<TransactionController> _log;

    public TransactionController(TransactionApplicationService svc, ILogger<TransactionController> log)
    {
        _svc = svc;
        _log = log;
    }

    // GET /api/transactions/{userId}
    /// <summary>
    /// Cursor-paginated transaction history for a user.
    /// Supports filtering by type (Deposit / InPlatformPayment / ExternalTransfer),
    /// status (Pending / Processing / Completed / Failed), and date range.
    /// </summary>
    [HttpGet("{userId:guid}")]
    [ProducesResponseType(typeof(PagedTransactionResponse), 200)]
    public async Task<IActionResult> GetUserTransactions(
        Guid userId,
        [FromQuery] string? type,
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? cursor,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _svc.GetUserTransactionsAsync(new TransactionQuery(
            userId, type, status, from, to, cursor,
            Math.Clamp(pageSize, 1, 100)), ct);

        return Ok(result);
    }

    // GET /api/transactions/{id}
    /// <summary>
    /// Returns a single transaction by ID.
    /// Requires the userId query param to enforce ownership.
    /// </summary>
    [HttpGet("detail/{id:guid}")]
    [ProducesResponseType(typeof(TransactionResponse), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetTransaction(
        Guid id, [FromQuery] Guid userId, CancellationToken ct)
    {
        try
        {
            var result = await _svc.GetTransactionAsync(id, userId, ct);
            return Ok(result);
        }
        catch (TransactionNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (TransactionNotOwnedException)
        {
            return Forbid();
        }
    }
}
