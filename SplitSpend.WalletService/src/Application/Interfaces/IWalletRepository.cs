using WalletService.Domain.Entities;

namespace WalletService.Application.Interfaces;

public interface IWalletRepository
{
    Task<Wallet?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<Wallet> GetByUserIdRequiredAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves both payer and recipient wallets in a single DB round-trip
    /// with pessimistic row locks, for use inside an atomic transaction.
    /// </summary>
    Task<(Wallet Payer, Wallet Recipient)> GetTwoWithLockAsync(
        Guid payerUserId, Guid recipientUserId, CancellationToken ct = default);

    Task AddAsync(Wallet wallet, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
