using BudgetService.Application.Interfaces;
using BudgetService.Domain.Entities;
using BudgetService.Domain.Enums;
using BudgetService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BudgetService.Infrastructure.Repositories;

public class BudgetRepository : IBudgetRepository
{
    private readonly BudgetDbContext _db;
    public BudgetRepository(BudgetDbContext db) => _db = db;

    public async Task<Budget?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.Budgets.FindAsync(new object[] { id }, ct);

    public async Task<Budget> GetByIdRequiredAsync(Guid id, CancellationToken ct = default)
        => await _db.Budgets.FindAsync(new object[] { id }, ct)
           ?? throw new BudgetNotFoundException(id);

    public async Task<List<Budget>> GetActiveByUserIdAsync(Guid userId, CancellationToken ct = default)
        => await _db.Budgets
            .Where(b => b.UserId == userId && b.Status == BudgetStatus.Active)
            .OrderBy(b => b.StartDate)
            .ToListAsync(ct);

    /// <summary>
    /// Returns active budgets whose date window covers the given date, FIFO order.
    /// </summary>
    public async Task<List<Budget>> GetActiveBudgetsForSpendAsync(
        Guid userId, DateTime date, CancellationToken ct = default)
        => await _db.Budgets
            .Where(b =>
                b.UserId == userId &&
                b.Status == BudgetStatus.Active &&
                b.StartDate <= date &&
                b.EndDate >= date)
            .OrderBy(b => b.StartDate) // FIFO: oldest first
            .ToListAsync(ct);

    public async Task<List<Budget>> GetExpiredActiveBudgetsAsync(
        DateTime today, CancellationToken ct = default)
        => await _db.Budgets
            .Where(b => b.Status == BudgetStatus.Active && b.EndDate < today)
            .ToListAsync(ct);

    public async Task<List<(Guid UserId, List<Budget> Budgets)>> GetUsersWithActiveBudgetsAsync(
        DateTime date, CancellationToken ct = default)
    {
        var budgets = await _db.Budgets
            .Where(b =>
                b.Status == BudgetStatus.Active &&
                b.StartDate <= date &&
                b.EndDate >= date)
            .OrderBy(b => b.UserId)
            .ThenBy(b => b.StartDate)
            .ToListAsync(ct);

        return budgets
            .GroupBy(b => b.UserId)
            .Select(g => (g.Key, g.ToList()))
            .ToList();
    }

    public async Task AddAsync(Budget budget, CancellationToken ct = default)
        => await _db.Budgets.AddAsync(budget, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class DailyBudgetRepository : IDailyBudgetRepository
{
    private readonly BudgetDbContext _db;
    public DailyBudgetRepository(BudgetDbContext db) => _db = db;

    public async Task<UserTotalDailyBudget?> GetDailyTotalAsync(
        Guid userId, DateTime date, CancellationToken ct = default)
        => await _db.UserTotalDailyBudgets
            .FirstOrDefaultAsync(r => r.UserId == userId && r.Date == date.Date, ct);

    public async Task<List<DailyBudgetRecord>> GetDailyRecordsAsync(
        Guid userId, DateTime date, CancellationToken ct = default)
        => await _db.DailyBudgetRecords
            .Where(r => r.UserId == userId && r.Date == date.Date)
            .OrderBy(r => r.BudgetId)
            .ToListAsync(ct);

    public async Task AddDailyTotalAsync(UserTotalDailyBudget record, CancellationToken ct = default)
        => await _db.UserTotalDailyBudgets.AddAsync(record, ct);

    public async Task AddDailyRecordAsync(DailyBudgetRecord record, CancellationToken ct = default)
        => await _db.DailyBudgetRecords.AddAsync(record, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class GiftBudgetRepository : IGiftBudgetRepository
{
    private readonly BudgetDbContext _db;
    public GiftBudgetRepository(BudgetDbContext db) => _db = db;

    public async Task<GiftBudget?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.GiftBudgets.FindAsync(new object[] { id }, ct);

    public async Task<List<GiftBudget>> GetBySenderAsync(
        Guid senderUserId, CancellationToken ct = default)
        => await _db.GiftBudgets
            .Where(g => g.SenderUserId == senderUserId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

    public async Task<List<GiftBudget>> GetByReceiverAsync(
        Guid receiverUserId, CancellationToken ct = default)
        => await _db.GiftBudgets
            .Where(g => g.ReceiverUserId == receiverUserId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(GiftBudget gift, CancellationToken ct = default)
        => await _db.GiftBudgets.AddAsync(gift, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class IdempotencyRepository : IIdempotencyRepository
{
    private readonly BudgetDbContext _db;
    public IdempotencyRepository(BudgetDbContext db) => _db = db;

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct);

    public async Task MarkAsync(string key, CancellationToken ct = default)
    {
        // Only add if not already present — prevents PK conflicts when called multiple times
        if (!await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct))
            _db.IdempotencyRecords.Add(new IdempotencyRecord { Key = key });
    }
}
