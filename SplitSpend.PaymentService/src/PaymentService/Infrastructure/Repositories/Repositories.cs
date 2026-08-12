using Microsoft.EntityFrameworkCore;
using PaymentService.Application.Interfaces;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Data;

namespace PaymentService.Infrastructure.Repositories;

public class PaymentLogRepository : IPaymentLogRepository
{
    private readonly PaymentDbContext _db;
    public PaymentLogRepository(PaymentDbContext db) => _db = db;

    public async Task<PaymentLog?> GetByReferenceAsync(
        string reference, CancellationToken ct = default)
        => await _db.PaymentLogs
            .FirstOrDefaultAsync(p => p.PaystackReference == reference, ct);

    public async Task<List<PaymentLog>> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default)
        => await _db.PaymentLogs
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(PaymentLog log, CancellationToken ct = default)
        => await _db.PaymentLogs.AddAsync(log, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class VirtualAccountRepository : IVirtualAccountRepository
{
    private readonly PaymentDbContext _db;
    public VirtualAccountRepository(PaymentDbContext db) => _db = db;

    public async Task<VirtualAccount?> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default)
        => await _db.VirtualAccounts
            .FirstOrDefaultAsync(v => v.UserId == userId, ct);

    public async Task<VirtualAccount?> GetByAccountNumberAsync(
        string accountNumber, CancellationToken ct = default)
        => await _db.VirtualAccounts
            .FirstOrDefaultAsync(v => v.AccountNumber == accountNumber, ct);

    public async Task<VirtualAccount?> GetByCustomerCodeAsync(
        string customerCode, CancellationToken ct = default)
        => await _db.VirtualAccounts
            .FirstOrDefaultAsync(v => v.PaystackCustomerCode == customerCode, ct);

    public async Task AddAsync(VirtualAccount account, CancellationToken ct = default)
        => await _db.VirtualAccounts.AddAsync(account, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}

public class IdempotencyRepository : IIdempotencyRepository
{
    private readonly PaymentDbContext _db;
    public IdempotencyRepository(PaymentDbContext db) => _db = db;

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct);

    public async Task MarkAsync(string key, CancellationToken ct = default)
    {
        if (!await _db.IdempotencyRecords.AnyAsync(r => r.Key == key, ct))
            _db.IdempotencyRecords.Add(new IdempotencyRecord { Key = key });
    }
}
