using BudgetService.Domain.Entities;

namespace BudgetService.Application.Interfaces;

public interface IBudgetRepository
{
    Task<Budget?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Budget> GetByIdRequiredAsync(Guid id, CancellationToken ct = default);
    Task<List<Budget>> GetActiveByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all active budgets whose date range overlaps today, ordered by StartDate ASC (FIFO).
    /// </summary>
    Task<List<Budget>> GetActiveBudgetsForSpendAsync(Guid userId, DateTime date, CancellationToken ct = default);

    /// <summary>
    /// Returns all budgets that have passed their EndDate but are still Active.
    /// Used by the end-of-day CRON to mark them Completed.
    /// </summary>
    Task<List<Budget>> GetExpiredActiveBudgetsAsync(DateTime today, CancellationToken ct = default);

    /// <summary>
    /// Returns one Budget per user that is Active on the given date, for CRON daily release.
    /// </summary>
    Task<List<(Guid UserId, List<Budget> Budgets)>> GetUsersWithActiveBudgetsAsync(
        DateTime date, CancellationToken ct = default);

    Task AddAsync(Budget budget, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IDailyBudgetRepository
{
    Task<UserTotalDailyBudget?> GetDailyTotalAsync(Guid userId, DateTime date, CancellationToken ct = default);
    Task<List<DailyBudgetRecord>> GetDailyRecordsAsync(Guid userId, DateTime date, CancellationToken ct = default);
    Task AddDailyTotalAsync(UserTotalDailyBudget record, CancellationToken ct = default);
    Task AddDailyRecordAsync(DailyBudgetRecord record, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IGiftBudgetRepository
{
    Task<GiftBudget?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<GiftBudget>> GetBySenderAsync(Guid senderUserId, CancellationToken ct = default);
    Task<List<GiftBudget>> GetByReceiverAsync(Guid receiverUserId, CancellationToken ct = default);
    Task AddAsync(GiftBudget gift, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IIdempotencyRepository
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task MarkAsync(string key, CancellationToken ct = default);
}

public interface IWalletServiceClient
{
    /// <summary>
    /// Sync REST call to Wallet Service to check MainBalance before budget creation.
    /// Throws InsufficientWalletBalanceException if balance is insufficient.
    /// </summary>
    Task<decimal> GetMainBalanceAsync(Guid userId, CancellationToken ct = default);
}
