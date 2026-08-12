using Microsoft.EntityFrameworkCore;
using TransactionService.Application.Interfaces;
using TransactionService.Domain.Entities;
using TransactionService.Domain.Enums;
using TransactionService.Infrastructure.Data;

namespace TransactionService.Infrastructure.Repositories;

public class TransactionRepository : ITransactionRepository
{
    private readonly TransactionDbContext _db;
    public TransactionRepository(TransactionDbContext db) => _db = db;

    public async Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.Transactions.FindAsync(new object[] { id }, ct);

    public async Task<Transaction> GetByIdRequiredAsync(Guid id, CancellationToken ct = default)
        => await _db.Transactions.FindAsync(new object[] { id }, ct)
           ?? throw new TransactionNotFoundException(id);

    /// <summary>
    /// Finds an open (Pending or Processing) transaction by its idempotency key.
    /// payerUserId = Guid.Empty means "match by key only" (used internally).
    /// </summary>
    public async Task<Transaction?> GetOpenInPlatformPaymentAsync(
        Guid payerUserId, string idempotencyKeyPrefix, CancellationToken ct = default)
    {
        var query = _db.Transactions
            .Where(t => t.IdempotencyKey == idempotencyKeyPrefix &&
                        (t.Status == TransactionStatus.Pending ||
                         t.Status == TransactionStatus.Processing));

        if (payerUserId != Guid.Empty)
            query = query.Where(t => t.UserId == payerUserId);

        return await query.FirstOrDefaultAsync(ct);
    }

    public async Task<(List<Transaction> Items, int TotalCount)> GetPagedAsync(
        Guid userId,
        string? type,
        string? status,
        DateTime? fromDate,
        DateTime? toDate,
        Guid? cursorId,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = _db.Transactions.Where(t => t.UserId == userId);

        if (!string.IsNullOrWhiteSpace(type) &&
            Enum.TryParse<TransactionType>(type, true, out var parsedType))
            query = query.Where(t => t.Type == parsedType);

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<TransactionStatus>(status, true, out var parsedStatus))
            query = query.Where(t => t.Status == parsedStatus);

        if (fromDate.HasValue)
            query = query.Where(t => t.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(t => t.CreatedAt <= toDate.Value);

        // Total count before cursor (for UI display)
        var totalCount = await query.CountAsync(ct);

        // Apply cursor: find the CreatedAt of the cursor item, return records older than it
        if (cursorId.HasValue)
        {
            var cursor = await _db.Transactions
                .Where(t => t.Id == cursorId.Value)
                .Select(t => t.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (cursor != default)
                query = query.Where(t => t.CreatedAt < cursor);
        }

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task AddAsync(Transaction transaction, CancellationToken ct = default)
        => await _db.Transactions.AddAsync(transaction, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class IdempotencyRepository : IIdempotencyRepository
{
    private readonly TransactionDbContext _db;
    public IdempotencyRepository(TransactionDbContext db) => _db = db;

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct);

    public async Task MarkAsync(string key, CancellationToken ct = default)
    {
        if (!await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct))
            _db.IdempotencyRecords.Add(new IdempotencyRecord { Key = key });
    }
}
