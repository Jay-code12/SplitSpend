using TransactionService.Application.DTOs;
using TransactionService.Application.Events;
using TransactionService.Application.Interfaces;
using TransactionService.Domain.Entities;
using TransactionService.Domain.Enums;
using TransactionService.Infrastructure.Data;

namespace TransactionService.Application.Services;

/// <summary>
/// Coordinates the transaction lifecycle state machine.
/// Every method is driven by a Kafka event from another service.
/// This service never initiates money movement — it only observes and records.
///
/// Idempotency strategy:
///   Each handler derives a deterministic key from the incoming event's own
///   idempotency key + a suffix describing the specific action taken.
///   This prevents double-processing if Kafka delivers the same event twice.
///
/// Correlation strategy:
///   In-platform payments are correlated by stripping the ":payer:budget" /
///   ":payer:main" / ":recipient" suffixes that Wallet Service appends to
///   the original vendor.payment.approved idempotency key. This lets us
///   find the open transaction when wallet.credited arrives for the recipient.
/// </summary>
public class TransactionApplicationService
{
    private readonly ITransactionRepository _transactions;
    private readonly IIdempotencyRepository _idempotency;
    private readonly IKafkaPublisher _kafka;
    private readonly TransactionDbContext _db;
    private readonly ILogger<TransactionApplicationService> _log;

    public TransactionApplicationService(
        ITransactionRepository transactions,
        IIdempotencyRepository idempotency,
        IKafkaPublisher kafka,
        TransactionDbContext db,
        ILogger<TransactionApplicationService> log)
    {
        _transactions = transactions;
        _idempotency  = idempotency;
        _kafka        = kafka;
        _db           = db;
        _log          = log;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DEPOSIT LIFECYCLE
    // payment.successful → Pending
    // wallet.credited    → Completed
    // payment.failed     → Failed
    // ═══════════════════════════════════════════════════════════════════════

    public async Task OnPaymentSuccessfulAsync(PaymentSuccessfulEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:deposit:create";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var transaction = Transaction.CreateDeposit(
                e.UserId, e.Amount, e.PaystackReference, e.IdempotencyKey);

            await _transactions.AddAsync(transaction, ct);
            await _idempotency.MarkAsync(key, ct);
            await _transactions.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await PublishCreatedAsync(transaction, key, ct);

            _log.LogInformation(
                "Deposit transaction {TxnId} created (Pending) for user {UserId}: ₦{Amount}",
                transaction.Id, e.UserId, e.Amount);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task OnPaymentFailedAsync(PaymentFailedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:deposit:fail";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        // Find the transaction by its creation idempotency key
        var transaction = await FindByCreationKeyAsync(e.IdempotencyKey, ct);
        if (transaction == null)
        {
            _log.LogWarning("payment.failed: no transaction found for key {Key}", e.IdempotencyKey);
            await MarkIdempotent(key, ct);
            return;
        }

        await FailTransactionAsync(transaction, e.Reason ?? "Payment failed", key, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IN-PLATFORM PAYMENT LIFECYCLE
    // vendor.payment.approved              → Pending
    // wallet.budget.debited / .main.debited → Processing
    // wallet.credited (recipient)          → Completed
    // wallet.insufficient_funds            → Failed
    // ═══════════════════════════════════════════════════════════════════════

    public async Task OnVendorPaymentApprovedAsync(VendorPaymentApprovedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:inplatform:create";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var transaction = Transaction.CreateInPlatformPayment(
                e.PayerUserId, e.RequesterUserId, e.Amount, e.IdempotencyKey);

            await _transactions.AddAsync(transaction, ct);
            await _idempotency.MarkAsync(key, ct);
            await _transactions.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await PublishCreatedAsync(transaction, key, ct);

            _log.LogInformation(
                "InPlatformPayment transaction {TxnId} created (Pending): ₦{Amount} payer={PayerId}",
                transaction.Id, e.Amount, e.PayerUserId);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>
    /// Wallet debited the payer's budget balance.
    /// Strip the ":payer:budget" suffix to find the original transaction.
    /// </summary>
    public async Task OnWalletBudgetDebitedAsync(WalletBudgetDebitedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:processing";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        // Wallet appends ":payer:budget" to the original idempotency key
        var baseKey    = StripPayerSuffix(e.IdempotencyKey);
        var transaction = await FindByCreationKeyAsync(baseKey, ct);
        if (transaction == null)
        {
            // Could be a non-payment budget debit (e.g. daily spend) — not an error
            _log.LogDebug(
                "wallet.budget.debited: no open InPlatformPayment found for key {Key} — skipping",
                baseKey);
            await MarkIdempotent(key, ct);
            return;
        }

        if (transaction.Type != TransactionType.InPlatformPayment)
        {
            await MarkIdempotent(key, ct);
            return;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            transaction.RecordDebitAndMarkProcessing(DebitSource.Budget, e.Amount, 0m);
            await _idempotency.MarkAsync(key, ct);
            await _transactions.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogInformation(
                "Transaction {TxnId} → Processing (budget debited ₦{Amount})",
                transaction.Id, e.Amount);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>
    /// Wallet debited the payer's main balance (fallback).
    /// Strip the ":payer:main" suffix to find the original transaction.
    /// </summary>
    public async Task OnWalletMainDebitedAsync(WalletMainDebitedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:processing:main";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        var baseKey     = StripPayerSuffix(e.IdempotencyKey);
        var transaction  = await FindByCreationKeyAsync(baseKey, ct);
        if (transaction == null || transaction.Type != TransactionType.InPlatformPayment)
        {
            await MarkIdempotent(key, ct);
            return;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // If already Processing (budget was partially used), add the main debit amount
            if (transaction.Status == TransactionStatus.Processing)
            {
                transaction.RecordDebitAndMarkProcessing(
                    DebitSource.Main,
                    transaction.BudgetDebited ?? 0m,
                    e.Amount);
            }
            else
            {
                transaction.RecordDebitAndMarkProcessing(DebitSource.Main, 0m, e.Amount);
            }

            await _idempotency.MarkAsync(key, ct);
            await _transactions.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogInformation(
                "Transaction {TxnId} → Processing (main debited ₦{Amount})",
                transaction.Id, e.Amount);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>
    /// wallet.credited is fired for both deposits and in-platform payment recipient credits.
    /// We determine which transaction to close by inspecting the counterparty and idempotency key.
    ///
    /// For deposits: the event's idempotency key matches the payment.successful key exactly.
    /// For in-platform payments: the event's key has a ":recipient" suffix from Wallet Service.
    /// </summary>
    public async Task OnWalletCreditedAsync(WalletCreditedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:complete";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        // Case 1: Deposit — credited event key matches original payment key exactly
        var transaction = await FindByCreationKeyAsync(e.IdempotencyKey, ct);

        // Case 2: InPlatformPayment — Wallet appends ":recipient" to the original key
        if (transaction == null && e.IdempotencyKey.EndsWith(":recipient"))
        {
            var baseKey  = e.IdempotencyKey[..^":recipient".Length];
            transaction  = await FindByCreationKeyAsync(baseKey, ct);
        }

        if (transaction == null)
        {
            _log.LogDebug(
                "wallet.credited: no matching transaction for key {Key} — likely a gift/refund, not tracked here",
                e.IdempotencyKey);
            await MarkIdempotent(key, ct);
            return;
        }

        await CompleteTransactionAsync(transaction, key, ct);
    }

    public async Task OnWalletInsufficientFundsAsync(WalletInsufficientFundsEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:fail:funds";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        var transaction = await FindByCreationKeyAsync(e.IdempotencyKey, ct);
        if (transaction == null)
        {
            await MarkIdempotent(key, ct);
            return;
        }

        await FailTransactionAsync(
            transaction,
            $"Insufficient funds. Main: ₦{e.MainBalance:N2}, Budget: ₦{e.BudgetBalance:N2}",
            key, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EXTERNAL TRANSFER LIFECYCLE
    // transfer.created   → Pending
    // transfer.completed → Completed
    // transfer.failed    → Failed
    // ═══════════════════════════════════════════════════════════════════════

    public async Task OnTransferCreatedAsync(TransferCreatedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:transfer:create";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var transaction = Transaction.CreateExternalTransfer(
                e.UserId, e.Amount, e.TransferId.ToString(), e.IdempotencyKey);

            await _transactions.AddAsync(transaction, ct);
            await _idempotency.MarkAsync(key, ct);
            await _transactions.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await PublishCreatedAsync(transaction, key, ct);

            _log.LogInformation(
                "ExternalTransfer transaction {TxnId} created (Pending): ₦{Amount} for user {UserId}",
                transaction.Id, e.Amount, e.UserId);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task OnTransferCompletedAsync(TransferCompletedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:transfer:complete";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        var transaction = await FindByCreationKeyAsync(e.IdempotencyKey, ct);
        if (transaction == null)
        {
            _log.LogWarning("transfer.completed: no transaction found for key {Key}", e.IdempotencyKey);
            await MarkIdempotent(key, ct);
            return;
        }

        await CompleteTransactionAsync(transaction, key, ct);
    }

    public async Task OnTransferFailedAsync(TransferFailedEvent e, CancellationToken ct)
    {
        var key = e.IdempotencyKey + ":txn:transfer:fail";
        if (await _idempotency.ExistsAsync(key, ct)) return;

        var transaction = await FindByCreationKeyAsync(e.IdempotencyKey, ct);
        if (transaction == null)
        {
            _log.LogWarning("transfer.failed: no transaction found for key {Key}", e.IdempotencyKey);
            await MarkIdempotent(key, ct);
            return;
        }

        await FailTransactionAsync(transaction, e.Reason, key, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // QUERIES
    // ═══════════════════════════════════════════════════════════════════════

    public async Task<TransactionResponse> GetTransactionAsync(
        Guid id, Guid userId, CancellationToken ct)
    {
        var transaction = await _transactions.GetByIdRequiredAsync(id, ct);
        if (transaction.UserId != userId)
            throw new TransactionNotOwnedException(id, userId);
        return MapToResponse(transaction);
    }

    public async Task<PagedTransactionResponse> GetUserTransactionsAsync(
        TransactionQuery query, CancellationToken ct)
    {
        var (items, totalCount) = await _transactions.GetPagedAsync(
            query.UserId, query.Type, query.Status,
            query.FromDate, query.ToDate,
            query.CursorId, query.PageSize + 1, ct);

        var hasMore = items.Count > query.PageSize;
        var page    = items.Take(query.PageSize).ToList();

        return new PagedTransactionResponse(
            page.Select(MapToResponse).ToList(),
            totalCount,
            hasMore ? page.Last().Id : null,
            hasMore);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task CompleteTransactionAsync(
        Transaction transaction, string idempotencyKey, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            transaction.Complete();
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _transactions.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.TransactionCompleted, new TransactionCompletedEvent(
                transaction.Id, transaction.UserId,
                transaction.Type.ToString(), transaction.Amount,
                transaction.DebitSource.ToString(),
                transaction.BudgetDebited, transaction.MainDebited,
                transaction.CounterpartyUserId,
                idempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Transaction {TxnId} COMPLETED ({Type}) ₦{Amount} for user {UserId}",
                transaction.Id, transaction.Type, transaction.Amount, transaction.UserId);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    private async Task FailTransactionAsync(
        Transaction transaction, string reason, string idempotencyKey, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            transaction.Fail(reason);
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _transactions.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.TransactionFailed, new TransactionFailedEvent(
                transaction.Id, transaction.UserId,
                transaction.Type.ToString(), transaction.Amount,
                reason, idempotencyKey, DateTime.UtcNow), ct);

            _log.LogWarning(
                "Transaction {TxnId} FAILED ({Type}): {Reason}",
                transaction.Id, transaction.Type, reason);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    private async Task PublishCreatedAsync(
        Transaction transaction, string idempotencyKey, CancellationToken ct)
    {
        await _kafka.PublishAsync(KafkaTopics.TransactionCreated, new TransactionCreatedEvent(
            transaction.Id, transaction.UserId,
            transaction.Type.ToString(), transaction.Amount,
            idempotencyKey, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Finds an open (Pending or Processing) transaction by its original creation idempotency key.
    /// </summary>
    private async Task<Transaction?> FindByCreationKeyAsync(string key, CancellationToken ct)
        => await _transactions.GetOpenInPlatformPaymentAsync(Guid.Empty, key, ct);

    /// <summary>
    /// Strips Wallet Service suffixes from idempotency keys to find the base key.
    /// Wallet appends ":payer:budget", ":payer:main", ":recipient" to the original key.
    /// </summary>
    private static string StripPayerSuffix(string key)
    {
        foreach (var suffix in new[] { ":payer:budget", ":payer:main", ":recipient" })
        {
            if (key.EndsWith(suffix))
                return key[..^suffix.Length];
        }
        return key;
    }

    private async Task MarkIdempotent(string key, CancellationToken ct)
    {
        await _idempotency.MarkAsync(key, ct);
        await _db.SaveChangesAsync(ct);
    }

    private static TransactionResponse MapToResponse(Transaction t) => new(
        t.Id, t.UserId,
        t.Type.ToString(), t.Status.ToString(),
        t.Amount, t.Currency,
        t.DebitSource.ToString(),
        t.BudgetDebited, t.MainDebited,
        t.CounterpartyUserId,
        t.PaystackReference, t.ExternalTransferId,
        t.FailureReason,
        t.ProcessingStartedAt, t.CompletedAt, t.FailedAt,
        t.CreatedAt);
}
