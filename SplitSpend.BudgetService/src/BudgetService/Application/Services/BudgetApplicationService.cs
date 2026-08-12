using BudgetService.Application.DTOs;
using BudgetService.Application.Events;
using BudgetService.Application.Interfaces;
using BudgetService.Domain.Entities;
using BudgetService.Domain.Enums;
using BudgetService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BudgetService.Application.Services;

/// <summary>
/// Core orchestration service for budget operations.
/// Budget Service never touches money directly — it validates, records state,
/// and emits Kafka events that Wallet Service acts on.
/// </summary>
public class BudgetApplicationService
{
    private readonly IBudgetRepository _budgets;
    private readonly IDailyBudgetRepository _daily;
    private readonly IGiftBudgetRepository _gifts;
    private readonly IIdempotencyRepository _idempotency;
    private readonly IWalletServiceClient _walletClient;
    private readonly IKafkaPublisher _kafka;
    private readonly BudgetDbContext _db;
    private readonly ILogger<BudgetApplicationService> _log;

    public BudgetApplicationService(
        IBudgetRepository budgets,
        IDailyBudgetRepository daily,
        IGiftBudgetRepository gifts,
        IIdempotencyRepository idempotency,
        IWalletServiceClient walletClient,
        IKafkaPublisher kafka,
        BudgetDbContext db,
        ILogger<BudgetApplicationService> log)
    {
        _budgets     = budgets;
        _daily       = daily;
        _gifts       = gifts;
        _idempotency = idempotency;
        _walletClient = walletClient;
        _kafka       = kafka;
        _db          = db;
        _log         = log;
    }

    // ── CREATE BUDGET ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a budget in Pending state, then emits budget.created so Wallet Service
    /// can transfer Main → Budget. Budget only becomes Active after wallet confirms.
    ///
    /// Flow:
    ///   1. Idempotency check
    ///   2. Sync REST call to Wallet Service — verify MainBalance >= totalAmount
    ///   3. Create Budget (Pending) + persist
    ///   4. Emit budget.created → Wallet transfers Main → Budget
    ///   5. Wallet emits wallet.budget.transfer.completed → WalletTransferCompletedConsumer activates
    /// </summary>
    public async Task<BudgetResponse> CreateBudgetAsync(CreateBudgetRequest req, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(req.IdempotencyKey, ct))
            throw new DuplicateIdempotencyKeyException(req.IdempotencyKey);

        // Step 1: Verify wallet balance before doing anything
        var mainBalance = await _walletClient.GetMainBalanceAsync(req.UserId, ct);
        if (mainBalance < req.TotalAmount)
            throw new InsufficientWalletBalanceException(req.TotalAmount, mainBalance);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var budget = Budget.Create(
                req.UserId, req.TotalAmount, req.DurationDays,
                req.StartDate, req.IdempotencyKey);

            await _budgets.AddAsync(budget, ct);
            await _idempotency.MarkAsync(req.IdempotencyKey, ct);
            await _budgets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Emit — Wallet Service will handle the actual money transfer
            await _kafka.PublishAsync(KafkaTopics.BudgetCreated, new BudgetCreatedEvent(
                budget.Id, budget.UserId, budget.TotalAmount, budget.DailyAmount,
                budget.DurationDays, budget.StartDate, budget.EndDate,
                budget.Source.ToString(), budget.GiftSenderId,
                req.IdempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Budget {BudgetId} created (Pending) for user {UserId} | ₦{Total} / {Days} days",
                budget.Id, budget.UserId, budget.TotalAmount, budget.DurationDays);

            return MapToResponse(budget);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── ACTIVATE (called by consumer when wallet confirms transfer) ───────────

    public async Task ActivateBudgetAsync(Guid userId, string walletIdempotencyKey, CancellationToken ct)
    {
        var activateKey = walletIdempotencyKey + ":activate";
        if (await _idempotency.ExistsAsync(activateKey, ct))
        {
            _log.LogWarning("Duplicate activate for wallet key {Key}", walletIdempotencyKey);
            return;
        }

        // Find the pending budget whose creation idempotency key matches the wallet event key
        var budget = await _db.Budgets
            .FirstOrDefaultAsync(b => b.UserId == userId && b.Status == BudgetStatus.Pending, ct);

        if (budget == null)
        {
            _log.LogWarning("No Pending budget found for user {UserId} to activate", userId);
            return;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            budget.Activate();
            await _idempotency.MarkAsync(activateKey, ct);
            await _budgets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.BudgetActivated, new BudgetActivatedEvent(
                budget.Id, budget.UserId, budget.TotalAmount, budget.DailyAmount,
                budget.StartDate, budget.EndDate,
                activateKey, DateTime.UtcNow), ct);

            _log.LogInformation("Budget {BudgetId} activated for user {UserId}", budget.Id, userId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── MARK FAILED (wallet transfer failed) ─────────────────────────────────

    public async Task MarkBudgetFailedAsync(Guid userId, string reason, string walletIdempotencyKey, CancellationToken ct)
    {
        var failKey = walletIdempotencyKey + ":fail";
        if (await _idempotency.ExistsAsync(failKey, ct))
            return;

        var budget = await _db.Budgets
            .FirstOrDefaultAsync(b => b.UserId == userId && b.Status == BudgetStatus.Pending, ct);

        if (budget == null) return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            budget.MarkFailed(reason);
            await _idempotency.MarkAsync(failKey, ct);
            await _budgets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogWarning("Budget {BudgetId} failed for user {UserId}: {Reason}", budget.Id, userId, reason);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── RECORD SPEND (wallet.budget.debited) ─────────────────────────────────

    /// <summary>
    /// Distributes spend across active budgets in FIFO order (oldest StartDate first).
    /// Only called when budget balance was debited — never for Main balance debits.
    /// This is why wallet.budget.debited and wallet.main.debited are separate events.
    /// </summary>
    public async Task RecordBudgetSpendAsync(
        Guid userId, decimal amount, string idempotencyKey, CancellationToken ct)
    {
        var spendKey = idempotencyKey + ":spend";
        if (await _idempotency.ExistsAsync(spendKey, ct))
            return;

        var today        = DateTime.UtcNow.Date;
        var activeBudgets = await _budgets.GetActiveBudgetsForSpendAsync(userId, today, ct);
        var dailyTotal   = await _daily.GetDailyTotalAsync(userId, today, ct);
        var dailyRecords = await _daily.GetDailyRecordsAsync(userId, today, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // FIFO: distribute spend across budgets oldest-first
            var remaining = amount;
            foreach (var budget in activeBudgets)
            {
                if (remaining <= 0) break;

                var consumed = budget.DeductSpend(remaining);
                remaining -= consumed;

                // Update the per-budget daily record for this day
                var dailyRecord = dailyRecords.FirstOrDefault(r => r.BudgetId == budget.Id);
                dailyRecord?.RecordSpend(consumed);

                if (budget.Status == BudgetStatus.Completed)
                {
                    _log.LogInformation("Budget {BudgetId} fully consumed for user {UserId}", budget.Id, userId);
                    // Fire-and-forget the completed event after tx commits
                }
            }

            // Update aggregate daily spend tracker
            dailyTotal?.RecordSpend(amount - remaining); // Only record what was actually deducted

            await _idempotency.MarkAsync(spendKey, ct);
            await _budgets.SaveChangesAsync(ct);
            await _daily.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Publish completed events for any budgets that just finished
            foreach (var budget in activeBudgets.Where(b => b.Status == BudgetStatus.Completed))
            {
                await _kafka.PublishAsync(KafkaTopics.BudgetCompleted, new BudgetCompletedEvent(
                    budget.Id, budget.UserId, budget.TotalAmount,
                    "FullyConsumed", spendKey + ":" + budget.Id, DateTime.UtcNow), ct);
            }

            _log.LogInformation(
                "Spend ₦{Amount} recorded for user {UserId} across {Count} budgets",
                amount, userId, activeBudgets.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── CANCEL BUDGET ────────────────────────────────────────────────────────

    public async Task<BudgetResponse> CancelBudgetAsync(CancelBudgetRequest req, CancellationToken ct)
    {
        var budget = await _budgets.GetByIdRequiredAsync(req.BudgetId, ct);

        if (budget.UserId != req.UserId)
            throw new BudgetNotOwnedException(req.BudgetId, req.UserId);

        var cancelKey = $"budget:cancel:{req.BudgetId}";
        if (await _idempotency.ExistsAsync(cancelKey, ct))
            throw new DuplicateIdempotencyKeyException(cancelKey);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var remaining = budget.RemainingTotal;
            budget.Cancel();
            await _idempotency.MarkAsync(cancelKey, ct);
            await _budgets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.BudgetCancelled, new BudgetCancelledEvent(
                budget.Id, budget.UserId, remaining,
                cancelKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Budget {BudgetId} cancelled for user {UserId} | ₦{Remaining} to be returned",
                budget.Id, budget.UserId, remaining);

            return MapToResponse(budget);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── SEND GIFT ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a GiftBudget record and emits gift.sent.
    /// Wallet Service handles the actual money transfer (debit sender, credit receiver).
    /// After that, a Budget is created for the receiver via a separate flow.
    /// </summary>
    public async Task<GiftBudgetResponse> SendGiftAsync(SendGiftRequest req, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(req.IdempotencyKey, ct))
            throw new DuplicateIdempotencyKeyException(req.IdempotencyKey);

        // Verify sender has enough balance
        var senderBalance = await _walletClient.GetMainBalanceAsync(req.SenderUserId, ct);
        if (senderBalance < req.Amount)
            throw new InsufficientWalletBalanceException(req.Amount, senderBalance);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var gift = GiftBudget.Create(
                req.SenderUserId, req.ReceiverUserId,
                req.Amount, req.DurationDays,
                req.IdempotencyKey, req.Message);

            await _gifts.AddAsync(gift, ct);
            await _idempotency.MarkAsync(req.IdempotencyKey, ct);
            await _gifts.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.GiftSent, new GiftSentEvent(
                gift.Id, gift.SenderUserId, gift.ReceiverUserId,
                gift.Amount, gift.DurationDays, gift.Message,
                req.IdempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Gift {GiftId} sent: ₦{Amount} from {Sender} to {Receiver}",
                gift.Id, gift.Amount, req.SenderUserId, req.ReceiverUserId);

            return MapGiftToResponse(gift);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── QUERIES ───────────────────────────────────────────────────────────────

    public async Task<BudgetResponse> GetBudgetAsync(Guid budgetId, Guid userId, CancellationToken ct)
    {
        var budget = await _budgets.GetByIdRequiredAsync(budgetId, ct);
        if (budget.UserId != userId)
            throw new BudgetNotOwnedException(budgetId, userId);
        return MapToResponse(budget);
    }

    public async Task<DailySummaryResponse> GetDailySummaryAsync(Guid userId, CancellationToken ct)
    {
        var today        = DateTime.UtcNow.Date;
        var dailyTotal   = await _daily.GetDailyTotalAsync(userId, today, ct);
        var dailyRecords = await _daily.GetDailyRecordsAsync(userId, today, ct);
        var activeBudgets = await _budgets.GetActiveByUserIdAsync(userId, ct);

        var breakdown = activeBudgets.Select(b =>
        {
            var rec = dailyRecords.FirstOrDefault(r => r.BudgetId == b.Id);
            return new ActiveBudgetDailySplit(
                b.Id,
                b.DailyAmount,
                rec?.SpentAmount ?? 0,
                rec?.UnusedAmount ?? b.DailyAmount);
        }).ToList();

        return new DailySummaryResponse(
            userId, today,
            dailyTotal?.TotalAllocated ?? 0,
            dailyTotal?.TotalSpent ?? 0,
            dailyTotal?.Remaining ?? 0,
            breakdown);
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────

    private static BudgetResponse MapToResponse(Budget b) => new(
        b.Id, b.UserId, b.TotalAmount, b.DailyAmount, b.RemainingTotal,
        b.DurationDays, b.StartDate, b.EndDate,
        b.Status.ToString(), b.Source.ToString(),
        b.GiftSenderId, b.CreatedAt);

    private static GiftBudgetResponse MapGiftToResponse(GiftBudget g) => new(
        g.Id, g.SenderUserId, g.ReceiverUserId, g.Amount, g.DurationDays,
        g.Status.ToString(), g.ResultingBudgetId, g.Message, g.CreatedAt);
}
