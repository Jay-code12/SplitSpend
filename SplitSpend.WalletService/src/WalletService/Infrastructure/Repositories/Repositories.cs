using Microsoft.EntityFrameworkCore;
using WalletService.Application.Interfaces;
using WalletService.Domain.Entities;
using WalletService.Infrastructure.Data;

namespace WalletService.Infrastructure.Repositories;

public class WalletRepository : IWalletRepository
{
    private readonly WalletDbContext _db;

    public WalletRepository(WalletDbContext db) => _db = db;

    public async Task<Wallet?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
        => await _db.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct);

    public async Task<Wallet> GetByUserIdRequiredAsync(Guid userId, CancellationToken ct = default)
        => await _db.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct)
           ?? throw new WalletNotFoundException(userId);

    /// <summary>
    /// Fetches both wallets in deterministic key order (lower Guid first) to prevent deadlocks.
    /// Uses UPDLOCK + ROWLOCK hints via raw SQL for SQL Server pessimistic locking.
    /// </summary>
    public async Task<(Wallet Payer, Wallet Recipient)> GetTwoWithLockAsync(
        Guid payerUserId, Guid recipientUserId, CancellationToken ct = default)
    {
        // Order by UserId to avoid deadlocks when two sessions lock the same two rows
        var ids = new[] { payerUserId, recipientUserId }.OrderBy(id => id).ToArray();

        var wallets = await _db.Wallets
            .FromSqlRaw(
                "SELECT * FROM Wallets WITH (UPDLOCK, ROWLOCK) WHERE UserId IN ({0}, {1})",
                ids[0], ids[1])
            .ToListAsync(ct);

        var payer     = wallets.FirstOrDefault(w => w.UserId == payerUserId)
                        ?? throw new WalletNotFoundException(payerUserId);
        var recipient = wallets.FirstOrDefault(w => w.UserId == recipientUserId)
                        ?? throw new WalletNotFoundException(recipientUserId);

        return (payer, recipient);
    }

    public async Task AddAsync(Wallet wallet, CancellationToken ct = default)
        => await _db.Wallets.AddAsync(wallet, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class LedgerRepository : ILedgerRepository
{
    private readonly WalletDbContext _db;

    public LedgerRepository(WalletDbContext db) => _db = db;

    public async Task AddAsync(WalletLedger entry, CancellationToken ct = default)
        => await _db.WalletLedger.AddAsync(entry, ct);

    public async Task AddRangeAsync(IEnumerable<WalletLedger> entries, CancellationToken ct = default)
        => await _db.WalletLedger.AddRangeAsync(entries, ct);

    public async Task<IReadOnlyList<WalletLedger>> GetPagedAsync(
        Guid userId,
        string? type,
        DateTime? fromDate,
        DateTime? toDate,
        Guid? cursorId,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = _db.WalletLedger.Where(l => l.UserId == userId);

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(l => EF.Property<string>(l, "EntryType") == type);

        if (fromDate.HasValue)
            query = query.Where(l => l.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(l => l.CreatedAt <= toDate.Value);

        if (cursorId.HasValue)
        {
            var cursor = await _db.WalletLedger.FindAsync(new object[] { cursorId.Value }, ct);
            if (cursor != null)
                query = query.Where(l => l.CreatedAt < cursor.CreatedAt);
        }

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(pageSize)
            .ToListAsync(ct);
    }
}

public class IdempotencyRepository : IIdempotencyRepository
{
    private readonly WalletDbContext _db;

    public IdempotencyRepository(WalletDbContext db) => _db = db;

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct);

    public async Task MarkAsync(string key, CancellationToken ct = default)
    {
        _db.IdempotencyRecords.Add(new IdempotencyRecord { Key = key, CreatedAt = DateTime.UtcNow });
        // SaveChanges is called by the outer unit of work; don't call it here.
    }
}
