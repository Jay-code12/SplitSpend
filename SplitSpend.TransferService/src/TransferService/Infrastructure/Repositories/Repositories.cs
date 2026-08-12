using Microsoft.EntityFrameworkCore;
using TransferService.Application.Interfaces;
using TransferService.Domain.Entities;
using TransferService.Domain.Enums;
using TransferService.Infrastructure.Data;

namespace TransferService.Infrastructure.Repositories;

public class TransferRepository : ITransferRepository
{
    private readonly TransferDbContext _db;
    public TransferRepository(TransferDbContext db) => _db = db;

    public async Task<BankTransfer?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.BankTransfers.FindAsync(new object[] { id }, ct);

    public async Task<BankTransfer> GetByIdRequiredAsync(Guid id, CancellationToken ct = default)
        => await _db.BankTransfers.FindAsync(new object[] { id }, ct)
           ?? throw new TransferNotFoundException(id);

    public async Task<BankTransfer?> GetByPaystackReferenceAsync(
        string reference, CancellationToken ct = default)
        => await _db.BankTransfers
            .FirstOrDefaultAsync(t => t.PaystackReference == reference, ct);

    public async Task<List<BankTransfer>> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default)
        => await _db.BankTransfers
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Returns Processing transfers that have been running for more than 24 hours.
    /// Used by the Hangfire timeout check job (every 30 minutes per MVP spec).
    /// </summary>
    public async Task<List<BankTransfer>> GetTimedOutTransfersAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        return await _db.BankTransfers
            .Where(t =>
                t.Status == TransferStatus.Processing &&
                t.ProcessingStartedAt.HasValue &&
                t.ProcessingStartedAt.Value < cutoff)
            .ToListAsync(ct);
    }

    public async Task AddAsync(BankTransfer transfer, CancellationToken ct = default)
        => await _db.BankTransfers.AddAsync(transfer, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class BeneficiaryRepository : IBeneficiaryRepository
{
    private readonly TransferDbContext _db;
    public BeneficiaryRepository(TransferDbContext db) => _db = db;

    public async Task<BankBeneficiary?> GetAsync(
        Guid userId, string accountNumber, string bankCode, CancellationToken ct = default)
        => await _db.BankBeneficiaries
            .FirstOrDefaultAsync(b =>
                b.UserId == userId &&
                b.AccountNumber == accountNumber &&
                b.BankCode == bankCode, ct);

    public async Task<List<BankBeneficiary>> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default)
        => await _db.BankBeneficiaries
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.UpdatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(BankBeneficiary beneficiary, CancellationToken ct = default)
        => await _db.BankBeneficiaries.AddAsync(beneficiary, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class IdempotencyRepository : IIdempotencyRepository
{
    private readonly TransferDbContext _db;
    public IdempotencyRepository(TransferDbContext db) => _db = db;

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct);

    public async Task MarkAsync(string key, CancellationToken ct = default)
    {
        if (!await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct))
            _db.IdempotencyRecords.Add(new IdempotencyRecord { Key = key });
    }
}
