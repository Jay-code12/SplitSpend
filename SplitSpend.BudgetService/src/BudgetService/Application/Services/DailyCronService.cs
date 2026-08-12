using BudgetService.Application.Events;
using BudgetService.Application.Interfaces;
using BudgetService.Domain.Entities;
using BudgetService.Domain.Enums;
using BudgetService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BudgetService.Application.Services;

/// <summary>
/// Hangfire-scheduled CRON jobs for daily budget lifecycle.
///
/// Job 1 — DailyRelease (runs 00:01 UTC daily):
///   For every user with active budgets today, emit budget.daily.released.
///   Wallet Service allocates the daily amount to UserTotalDailyBudget.
///
/// Job 2 — DailyExpiry (runs 23:55 UTC daily):
///   For every user with active budgets, calculate unused daily amount.
///   Emit budget.daily.expired → Wallet returns unused funds to Main Balance.
///   Also auto-completes any budgets whose EndDate has passed.
///
/// Alert rule from MVP spec: If DailyRelease job does not fire by 06:10 AM → Critical alert.
/// </summary>
public class DailyCronService
{
    private readonly IBudgetRepository _budgets;
    private readonly IDailyBudgetRepository _daily;
    private readonly IIdempotencyRepository _idempotency;
    private readonly IKafkaPublisher _kafka;
    private readonly BudgetDbContext _db;
    private readonly ILogger<DailyCronService> _log;

    public DailyCronService(
        IBudgetRepository budgets,
        IDailyBudgetRepository daily,
        IIdempotencyRepository idempotency,
        IKafkaPublisher kafka,
        BudgetDbContext db,
        ILogger<DailyCronService> log)
    {
        _budgets     = budgets;
        _daily       = daily;
        _idempotency = idempotency;
        _kafka       = kafka;
        _db          = db;
        _log         = log;
    }

    // ── JOB 1: Daily Release ──────────────────────────────────────────────────

    /// <summary>
    /// Runs at 00:01 UTC every day via Hangfire recurring job.
    /// Creates DailyBudgetRecord rows for each active budget and emits budget.daily.released
    /// so Wallet Service can allocate the daily amount.
    /// </summary>
    public async Task RunDailyReleaseAsync(CancellationToken ct = default)
    {
        var today    = DateTime.UtcNow.Date;
        var jobKey   = $"cron:daily-release:{today:yyyy-MM-dd}";

        if (await _idempotency.ExistsAsync(jobKey, ct))
        {
            _log.LogWarning("Daily release for {Date} already ran — skipping", today);
            return;
        }

        _log.LogInformation("=== Daily Release CRON started for {Date} ===", today);

        var userBudgetGroups = await _budgets.GetUsersWithActiveBudgetsAsync(today, ct);
        var successCount = 0;
        var errors = new List<string>();

        foreach (var (userId, activeBudgets) in userBudgetGroups)
        {
            try
            {
                await ProcessDailyReleaseForUserAsync(userId, activeBudgets, today, ct);
                successCount++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Daily release failed for user {UserId}", userId);
                errors.Add($"User {userId}: {ex.Message}");
            }
        }

        await _idempotency.MarkAsync(jobKey, ct);
        // Note: idempotency mark needs its own SaveChanges since the per-user transactions are scoped separately
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "=== Daily Release CRON complete: {Success}/{Total} users, {Errors} errors ===",
            successCount, userBudgetGroups.Count, errors.Count);
    }

    private async Task ProcessDailyReleaseForUserAsync(
        Guid userId, List<Budget> activeBudgets, DateTime today, CancellationToken ct)
    {
        var releaseKey = $"daily-release:{userId}:{today:yyyy-MM-dd}";
        if (await _idempotency.ExistsAsync(releaseKey, ct))
            return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var splits        = new List<BudgetDailySplit>();
            var totalDaily    = 0m;
            var existingTotal = await _daily.GetDailyTotalAsync(userId, today, ct);

            foreach (var budget in activeBudgets)
            {
                // Create per-budget daily record
                var dailyRecord = DailyBudgetRecord.Create(
                    budget.Id, userId, today, budget.DailyAmount);
                await _daily.AddDailyRecordAsync(dailyRecord, ct);

                splits.Add(new BudgetDailySplit(budget.Id, budget.DailyAmount));
                totalDaily += budget.DailyAmount;
            }

            // Create or update daily total record
            if (existingTotal == null)
            {
                var totalRecord = UserTotalDailyBudget.Create(userId, today, totalDaily);
                await _daily.AddDailyTotalAsync(totalRecord, ct);
            }
            else
            {
                existingTotal.AddAllocation(totalDaily);
            }

            await _idempotency.MarkAsync(releaseKey, ct);
            await _daily.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.BudgetDailyReleased, new BudgetDailyReleasedEvent(
                userId, today, totalDaily, splits,
                releaseKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Daily release for user {UserId}: ₦{Total} across {Count} budgets",
                userId, totalDaily, activeBudgets.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── JOB 2: Daily Expiry ───────────────────────────────────────────────────

    /// <summary>
    /// Runs at 23:55 UTC every day via Hangfire recurring job.
    /// For each user with active budgets, calculates unused daily allocation
    /// and emits budget.daily.expired → Wallet returns unused amount to Main Balance.
    /// Also auto-completes budgets whose EndDate has passed.
    /// </summary>
    public async Task RunDailyExpiryAsync(CancellationToken ct = default)
    {
        var today  = DateTime.UtcNow.Date;
        var jobKey = $"cron:daily-expiry:{today:yyyy-MM-dd}";

        if (await _idempotency.ExistsAsync(jobKey, ct))
        {
            _log.LogWarning("Daily expiry for {Date} already ran — skipping", today);
            return;
        }

        _log.LogInformation("=== Daily Expiry CRON started for {Date} ===", today);

        var userBudgetGroups  = await _budgets.GetUsersWithActiveBudgetsAsync(today, ct);
        var expiredBudgets    = await _budgets.GetExpiredActiveBudgetsAsync(today, ct);
        var errors            = new List<string>();
        var successCount      = 0;

        // Auto-complete budgets whose end date was yesterday
        foreach (var budget in expiredBudgets)
        {
            try
            {
                await AutoCompleteBudgetAsync(budget, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Auto-complete failed for budget {BudgetId}", budget.Id);
                errors.Add($"Auto-complete budget {budget.Id}: {ex.Message}");
            }
        }

        // Return unused daily amounts to Main Balance
        foreach (var (userId, activeBudgets) in userBudgetGroups)
        {
            try
            {
                await ProcessDailyExpiryForUserAsync(userId, today, ct);
                successCount++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Daily expiry failed for user {UserId}", userId);
                errors.Add($"User {userId}: {ex.Message}");
            }
        }

        await _idempotency.MarkAsync(jobKey, ct);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "=== Daily Expiry CRON complete: {Success}/{Total} users, {Expired} auto-completed, {Errors} errors ===",
            successCount, userBudgetGroups.Count, expiredBudgets.Count, errors.Count);
    }

    private async Task ProcessDailyExpiryForUserAsync(
        Guid userId, DateTime today, CancellationToken ct)
    {
        var expiryKey = $"daily-expiry:{userId}:{today:yyyy-MM-dd}";
        if (await _idempotency.ExistsAsync(expiryKey, ct))
            return;

        var dailyRecords = await _daily.GetDailyRecordsAsync(userId, today, ct);
        var totalUnused  = dailyRecords.Where(r => !r.IsExpired).Sum(r => r.UnusedAmount);

        if (totalUnused <= 0)
        {
            _log.LogDebug("No unused daily budget for user {UserId} on {Date}", userId, today);
            await _idempotency.MarkAsync(expiryKey, ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var record in dailyRecords.Where(r => !r.IsExpired))
                record.MarkExpired();

            await _idempotency.MarkAsync(expiryKey, ct);
            await _daily.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Wallet Service will credit Main Balance when it consumes this event
            await _kafka.PublishAsync(KafkaTopics.BudgetDailyExpired, new BudgetDailyExpiredEvent(
                userId, today, totalUnused,
                expiryKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Daily expiry for user {UserId}: ₦{Unused} returned to Main Balance",
                userId, totalUnused);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task AutoCompleteBudgetAsync(Budget budget, CancellationToken ct)
    {
        var completeKey = $"budget:auto-complete:{budget.Id}";
        if (await _idempotency.ExistsAsync(completeKey, ct))
            return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            budget.Complete();
            await _idempotency.MarkAsync(completeKey, ct);
            await _budgets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.BudgetCompleted, new BudgetCompletedEvent(
                budget.Id, budget.UserId, budget.TotalAmount,
                "EndDateReached", completeKey, DateTime.UtcNow), ct);

            _log.LogInformation("Budget {BudgetId} auto-completed (end date reached)", budget.Id);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
