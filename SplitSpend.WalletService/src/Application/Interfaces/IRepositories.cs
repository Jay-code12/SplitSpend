using WalletService.Domain.Entities;

namespace WalletService.Application.Interfaces;

public interface ILedgerRepository
{
    Task AddAsync(WalletLedger entry, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<WalletLedger> entries, CancellationToken ct = default);

    Task<IReadOnlyList<WalletLedger>> GetPagedAsync(
        Guid userId,
        string? type,
        DateTime? fromDate,
        DateTime? toDate,
        Guid? cursorId,
        int pageSize,
        CancellationToken ct = default);
}

public interface IIdempotencyRepository
{
    /// <summary>
    /// Returns true if the key already exists (duplicate), false if newly inserted.
    /// Uses DB-level unique constraint — thread-safe and race-condition-proof.
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task MarkAsync(string key, CancellationToken ct = default);
}
