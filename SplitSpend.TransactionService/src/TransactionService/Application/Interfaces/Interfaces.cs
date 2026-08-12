using TransactionService.Domain.Entities;

namespace TransactionService.Application.Interfaces;

public interface ITransactionRepository
{
    Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Transaction> GetByIdRequiredAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Finds the open (Pending/Processing) InPlatformPayment transaction for a payer
    /// that matches the given idempotency key prefix. Used to complete it when
    /// wallet.credited arrives for the recipient.
    /// </summary>
    Task<Transaction?> GetOpenInPlatformPaymentAsync(
        Guid payerUserId, string idempotencyKeyPrefix, CancellationToken ct = default);

    /// <summary>
    /// Cursor-based paged query, most recent first.
    /// </summary>
    Task<(List<Transaction> Items, int TotalCount)> GetPagedAsync(
        Guid userId,
        string? type,
        string? status,
        DateTime? fromDate,
        DateTime? toDate,
        Guid? cursorId,
        int pageSize,
        CancellationToken ct = default);

    Task AddAsync(Transaction transaction, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IIdempotencyRepository
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task MarkAsync(string key, CancellationToken ct = default);
}

public interface IKafkaPublisher
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default) where T : class;
}
