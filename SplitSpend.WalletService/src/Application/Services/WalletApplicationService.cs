using Microsoft.EntityFrameworkCore;
using WalletService.Application.DTOs;
using WalletService.Application.Events;
using WalletService.Application.Interfaces;
using WalletService.Domain.Entities;
using WalletService.Domain.Enums;
using WalletService.Infrastructure.Data;

namespace WalletService.Application.Services;

/// <summary>
/// Core application service for all wallet operations.
/// All money moves happen here. Idempotency is enforced before any ledger write.
/// Pessimistic locking is used for debit operations to prevent race conditions.
/// </summary>
public class WalletApplicationService
{
    private readonly IWalletRepository _wallets;
    private readonly ILedgerRepository _ledger;
    private readonly IIdempotencyRepository _idempotency;
    private readonly IKafkaPublisher _kafka;
    private readonly WalletDbContext _db;
    private readonly ILogger<WalletApplicationService> _log;

    public WalletApplicationService(
        IWalletRepository wallets,
        ILedgerRepository ledger,
        IIdempotencyRepository idempotency,
        IKafkaPublisher kafka,
        WalletDbContext db,
        ILogger<WalletApplicationService> log)
    {
        _wallets = wallets;
        _ledger = ledger;
        _idempotency = idempotency;
        _kafka = kafka;
        _db = db;
        _log = log;
    }

    // ── GET BALANCE ──────────────────────────────────────────────────────────

    public async Task<WalletBalanceResponse> GetBalanceAsync(Guid userId, CancellationToken ct)
    {
        var wallet = await _wallets.GetByUserIdRequiredAsync(userId, ct);
        return new WalletBalanceResponse(
            wallet.Id, wallet.UserId,
            wallet.MainBalance, wallet.BudgetBalance,
            wallet.Currency, wallet.Status.ToString());
    }

    // ── CREDIT ───────────────────────────────────────────────────────────────

    public async Task<CreditResponse> CreditAsync(CreditRequest req, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(req.IdempotencyKey, ct))
        {
            _log.LogWarning("Duplicate credit idempotency key {Key} for user {UserId}", req.IdempotencyKey, req.UserId);
            throw new DuplicateIdempotencyKeyException(req.IdempotencyKey);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var wallet = await _wallets.GetByUserIdRequiredAsync(req.UserId, ct);

            var mainBefore   = wallet.MainBalance;
            var budgetBefore = wallet.BudgetBalance;

            if (req.TargetBalance == BalanceType.Main)
                wallet.CreditMain(req.Amount);
            else
                wallet.CreditBudget(req.Amount);

            var entry = WalletLedger.CreateCredit(
                wallet.Id, wallet.UserId, req.Amount,
                mainBefore, budgetBefore,
                wallet.MainBalance, wallet.BudgetBalance,
                req.IdempotencyKey,
                req.CounterpartyId, req.Reference, req.Description);

            await _ledger.AddAsync(entry, ct);
            await _idempotency.MarkAsync(req.IdempotencyKey, ct);
            await _wallets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.WalletCredited, new WalletCreditedEvent(
                wallet.Id, wallet.UserId, req.Amount,
                wallet.MainBalance, wallet.BudgetBalance,
                req.CounterpartyId, req.Reference,
                req.IdempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation("Wallet credited {Amount} for user {UserId} via {Key}",
                req.Amount, req.UserId, req.IdempotencyKey);

            return new CreditResponse(entry.Id, req.Amount, wallet.MainBalance, wallet.BudgetBalance, req.IdempotencyKey);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── DEBIT (budget-first) ─────────────────────────────────────────────────

    public async Task<DebitResponse> DebitAsync(DebitRequest req, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(req.IdempotencyKey, ct))
        {
            _log.LogWarning("Duplicate debit idempotency key {Key}", req.IdempotencyKey);
            throw new DuplicateIdempotencyKeyException(req.IdempotencyKey);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var wallet = await _wallets.GetByUserIdRequiredAsync(req.UserId, ct);

            if (wallet.Status == WalletStatus.Suspended)
                throw new WalletSuspendedException(req.UserId);

            var mainBefore   = wallet.MainBalance;
            var budgetBefore = wallet.BudgetBalance;

            (decimal budgetDebited, decimal mainDebited) result;
            try
            {
                result = wallet.DebitBudgetFirst(req.Amount);
            }
            catch (InsufficientFundsException ex)
            {
                await tx.RollbackAsync(ct);
                _log.LogWarning("Insufficient funds for user {UserId}: {Message}", req.UserId, ex.Message);

                await _kafka.PublishAsync(KafkaTopics.WalletInsufficientFunds, new WalletInsufficientFundsEvent(
                    wallet.Id, wallet.UserId, req.Amount,
                    wallet.MainBalance, wallet.BudgetBalance,
                    req.IdempotencyKey, DateTime.UtcNow), ct);

                throw;
            }

            // Write ledger entries for each balance used
            var entries = new List<WalletLedger>();

            if (result.BudgetDebited > 0)
            {
                entries.Add(WalletLedger.CreateDebit(
                    wallet.Id, wallet.UserId, result.BudgetDebited, DebitSource.Budget,
                    mainBefore, budgetBefore,
                    wallet.MainBalance, wallet.BudgetBalance,
                    req.IdempotencyKey + ":budget",
                    req.CounterpartyId, req.Reference, req.Description));
            }

            if (result.MainDebited > 0)
            {
                entries.Add(WalletLedger.CreateDebit(
                    wallet.Id, wallet.UserId, result.MainDebited, DebitSource.Main,
                    mainBefore, budgetBefore,
                    wallet.MainBalance, wallet.BudgetBalance,
                    req.IdempotencyKey + ":main",
                    req.CounterpartyId, req.Reference, req.Description));
            }

            await _ledger.AddRangeAsync(entries, ct);
            await _idempotency.MarkAsync(req.IdempotencyKey, ct);
            await _wallets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Publish typed events per balance used
            if (result.BudgetDebited > 0)
            {
                await _kafka.PublishAsync(KafkaTopics.WalletBudgetDebited, new WalletBudgetDebitedEvent(
                    wallet.Id, wallet.UserId, result.BudgetDebited,
                    wallet.BudgetBalance,
                    req.CounterpartyId, req.Reference,
                    req.IdempotencyKey, DateTime.UtcNow), ct);
            }

            if (result.MainDebited > 0)
            {
                await _kafka.PublishAsync(KafkaTopics.WalletMainDebited, new WalletMainDebitedEvent(
                    wallet.Id, wallet.UserId, result.MainDebited,
                    wallet.MainBalance,
                    req.CounterpartyId, req.Reference,
                    req.IdempotencyKey, DateTime.UtcNow), ct);
            }

            return new DebitResponse(
                entries[0].Id,
                req.Amount,
                result.BudgetDebited,
                result.MainDebited,
                wallet.MainBalance,
                wallet.BudgetBalance,
                req.IdempotencyKey);
        }
        catch (InsufficientFundsException)
        {
            throw; // already rolled back and published event above
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── IN-PLATFORM ATOMIC PAY ───────────────────────────────────────────────

    /// <summary>
    /// Atomically debits the payer (budget-first) and credits the recipient's Main Balance.
    /// Both operations occur within a single DB transaction with pessimistic locking.
    /// No Paystack involved. No Payment Service involved. Settlement is instant.
    /// </summary>
    public async Task<InPlatformPayResponse> PayAsync(InPlatformPayRequest req, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(req.IdempotencyKey, ct))
            throw new DuplicateIdempotencyKeyException(req.IdempotencyKey);

        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var (payer, recipient) = await _wallets.GetTwoWithLockAsync(
                req.PayerUserId, req.RecipientUserId, ct);

            if (payer.Status == WalletStatus.Suspended)
                throw new WalletSuspendedException(req.PayerUserId);

            var payerMainBefore   = payer.MainBalance;
            var payerBudgetBefore = payer.BudgetBalance;
            var recipientMainBefore = recipient.MainBalance;

            // Debit payer — budget-first
            (decimal budgetDebited, decimal mainDebited) payerResult;
            try
            {
                payerResult = payer.DebitBudgetFirst(req.Amount);
            }
            catch (InsufficientFundsException)
            {
                await tx.RollbackAsync(ct);
                await _kafka.PublishAsync(KafkaTopics.WalletInsufficientFunds, new WalletInsufficientFundsEvent(
                    payer.Id, payer.UserId, req.Amount,
                    payer.MainBalance, payer.BudgetBalance,
                    req.IdempotencyKey, DateTime.UtcNow), ct);
                throw;
            }

            // Credit recipient Main Balance
            recipient.CreditMain(req.Amount);

            var payerEntries = new List<WalletLedger>();

            if (payerResult.BudgetDebited > 0)
                payerEntries.Add(WalletLedger.CreateDebit(
                    payer.Id, payer.UserId, payerResult.BudgetDebited, DebitSource.Budget,
                    payerMainBefore, payerBudgetBefore,
                    payer.MainBalance, payer.BudgetBalance,
                    req.IdempotencyKey + ":payer:budget",
                    req.RecipientUserId, req.Reference,
                    "In-platform payment debit (budget)"));

            if (payerResult.MainDebited > 0)
                payerEntries.Add(WalletLedger.CreateDebit(
                    payer.Id, payer.UserId, payerResult.MainDebited, DebitSource.Main,
                    payerMainBefore, payerBudgetBefore,
                    payer.MainBalance, payer.BudgetBalance,
                    req.IdempotencyKey + ":payer:main",
                    req.RecipientUserId, req.Reference,
                    "In-platform payment debit (main fallback)"));

            var recipientEntry = WalletLedger.CreateCredit(
                recipient.Id, recipient.UserId, req.Amount,
                recipientMainBefore, recipient.BudgetBalance,
                recipient.MainBalance, recipient.BudgetBalance,
                req.IdempotencyKey + ":recipient",
                req.PayerUserId, req.Reference,
                "In-platform payment received");

            await _ledger.AddRangeAsync(payerEntries, ct);
            await _ledger.AddAsync(recipientEntry, ct);
            await _idempotency.MarkAsync(req.IdempotencyKey, ct);
            await _wallets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Publish debit event(s) for payer
            if (payerResult.BudgetDebited > 0)
                await _kafka.PublishAsync(KafkaTopics.WalletBudgetDebited, new WalletBudgetDebitedEvent(
                    payer.Id, payer.UserId, payerResult.BudgetDebited,
                    payer.BudgetBalance, req.RecipientUserId, req.Reference,
                    req.IdempotencyKey, DateTime.UtcNow), ct);

            if (payerResult.MainDebited > 0)
                await _kafka.PublishAsync(KafkaTopics.WalletMainDebited, new WalletMainDebitedEvent(
                    payer.Id, payer.UserId, payerResult.MainDebited,
                    payer.MainBalance, req.RecipientUserId, req.Reference,
                    req.IdempotencyKey, DateTime.UtcNow), ct);

            // Publish credit event for recipient — Transaction Service listens to complete the tx
            await _kafka.PublishAsync(KafkaTopics.WalletCredited, new WalletCreditedEvent(
                recipient.Id, recipient.UserId, req.Amount,
                recipient.MainBalance, recipient.BudgetBalance,
                req.PayerUserId, req.Reference,
                req.IdempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "In-platform pay complete: {Amount} from {Payer} to {Recipient} | budget={B} main={M}",
                req.Amount, req.PayerUserId, req.RecipientUserId,
                payerResult.BudgetDebited, payerResult.MainDebited);

            return new InPlatformPayResponse(
                payerEntries[0].Id,
                recipientEntry.Id,
                req.Amount,
                payerResult.BudgetDebited,
                payerResult.MainDebited,
                payer.MainBalance,
                payer.BudgetBalance,
                recipient.MainBalance,
                req.IdempotencyKey);
        }
        catch (InsufficientFundsException)
        {
            throw;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── INTERNAL TRANSFER (Main ↔ Budget) ────────────────────────────────────

    public async Task<InternalTransferResponse> InternalTransferAsync(
        InternalTransferRequest req, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(req.IdempotencyKey, ct))
            throw new DuplicateIdempotencyKeyException(req.IdempotencyKey);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var wallet = await _wallets.GetByUserIdRequiredAsync(req.UserId, ct);
            var mainBefore   = wallet.MainBalance;
            var budgetBefore = wallet.BudgetBalance;

            bool isMainToBudget = req.Direction == "MainToBudget";
            string? failureReason = null;

            try
            {
                if (isMainToBudget)
                    wallet.TransferMainToBudget(req.Amount);
                else
                    wallet.TransferBudgetToMain(req.Amount);
            }
            catch (InsufficientFundsException ex)
            {
                await tx.RollbackAsync(ct);
                failureReason = ex.Message;

                if (isMainToBudget)
                    await _kafka.PublishAsync(KafkaTopics.WalletBudgetTransferFailed,
                        new WalletBudgetTransferFailedEvent(
                            wallet.Id, wallet.UserId, req.Amount,
                            failureReason, req.IdempotencyKey, DateTime.UtcNow), ct);

                return new InternalTransferResponse(
                    Guid.Empty, req.Direction, req.Amount,
                    wallet.MainBalance, wallet.BudgetBalance,
                    false, failureReason);
            }

            var direction = isMainToBudget
                ? LedgerEntryType.InternalTransferOut
                : LedgerEntryType.InternalTransferIn;

            var entry = WalletLedger.CreateInternalTransfer(
                wallet.Id, wallet.UserId, req.Amount, direction,
                mainBefore, budgetBefore,
                wallet.MainBalance, wallet.BudgetBalance,
                req.IdempotencyKey, req.Description);

            await _ledger.AddAsync(entry, ct);
            await _idempotency.MarkAsync(req.IdempotencyKey, ct);
            await _wallets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            if (isMainToBudget)
                await _kafka.PublishAsync(KafkaTopics.WalletBudgetTransferComplete,
                    new WalletBudgetTransferCompletedEvent(
                        wallet.Id, wallet.UserId, req.Amount,
                        wallet.MainBalance, wallet.BudgetBalance,
                        req.IdempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Internal transfer {Dir} {Amount} for user {UserId}",
                req.Direction, req.Amount, req.UserId);

            return new InternalTransferResponse(
                entry.Id, req.Direction, req.Amount,
                wallet.MainBalance, wallet.BudgetBalance, true);
        }
        catch (DuplicateIdempotencyKeyException) { throw; }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── EXTERNAL TRANSFER PRE-DEBIT ──────────────────────────────────────────

    /// <summary>
    /// Pre-debits MainBalance for an external bank transfer.
    /// Emits wallet.main.transfer.initiated — Transfer Service waits for this before calling Paystack.
    /// </summary>
    public async Task PreDebitForExternalTransferAsync(
        Guid userId, decimal amount, string transferReference, string idempotencyKey, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(idempotencyKey, ct))
            throw new DuplicateIdempotencyKeyException(idempotencyKey);

        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var wallet = await _wallets.GetByUserIdRequiredAsync(userId, ct);
            var mainBefore   = wallet.MainBalance;
            var budgetBefore = wallet.BudgetBalance;

            wallet.DebitMain(amount); // Throws InsufficientFundsException if not enough

            var entry = WalletLedger.CreateDebit(
                wallet.Id, wallet.UserId, amount, DebitSource.Main,
                mainBefore, budgetBefore,
                wallet.MainBalance, wallet.BudgetBalance,
                idempotencyKey, null, transferReference,
                "External bank transfer pre-debit");

            await _ledger.AddAsync(entry, ct);
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _wallets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.WalletMainTransferInitiated,
                new WalletMainTransferInitiatedEvent(
                    wallet.Id, wallet.UserId, amount,
                    wallet.MainBalance, transferReference,
                    idempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Pre-debit {Amount} for external transfer {Ref} for user {UserId}",
                amount, transferReference, userId);
        }
        catch (InsufficientFundsException)
        {
            await tx.RollbackAsync(ct);
            await _kafka.PublishAsync(KafkaTopics.WalletInsufficientFunds, new WalletInsufficientFundsEvent(
                Guid.Empty, userId, amount, 0, 0, idempotencyKey, DateTime.UtcNow), ct);
            throw;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── REVERSAL (external transfer failed) ──────────────────────────────────

    public async Task ReverseExternalTransferAsync(
        Guid userId, decimal amount, string transferReference, string idempotencyKey, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(idempotencyKey, ct))
            return; // Already reversed — idempotent

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var wallet = await _wallets.GetByUserIdRequiredAsync(userId, ct);
            var mainBefore   = wallet.MainBalance;
            var budgetBefore = wallet.BudgetBalance;

            wallet.CreditMain(amount);

            var entry = WalletLedger.CreateCredit(
                wallet.Id, wallet.UserId, amount,
                mainBefore, budgetBefore,
                wallet.MainBalance, wallet.BudgetBalance,
                idempotencyKey, null, transferReference,
                "External bank transfer reversal");

            await _ledger.AddAsync(entry, ct);
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _wallets.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.WalletCredited, new WalletCreditedEvent(
                wallet.Id, wallet.UserId, amount,
                wallet.MainBalance, wallet.BudgetBalance,
                null, transferReference,
                idempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Reversed {Amount} for failed external transfer {Ref} for user {UserId}",
                amount, transferReference, userId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── LEDGER QUERY ─────────────────────────────────────────────────────────

    public async Task<PagedLedgerResponse> GetLedgerAsync(LedgerQueryRequest req, CancellationToken ct)
    {
        var entries = await _ledger.GetPagedAsync(
            req.UserId, req.Type, req.FromDate, req.ToDate,
            req.CursorId, req.PageSize + 1, ct);

        var hasMore = entries.Count > req.PageSize;
        var page    = entries.Take(req.PageSize).ToList();

        return new PagedLedgerResponse(
            page.Select(e => new LedgerEntryResponse(
                e.Id, e.EntryType.ToString(), e.DebitSource?.ToString(),
                e.Amount, e.Currency,
                e.MainBalanceBefore, e.BudgetBalanceBefore,
                e.MainBalanceAfter, e.BudgetBalanceAfter,
                e.CounterpartyId, e.TransactionReference,
                e.Description, e.CreatedAt)).ToList(),
            hasMore ? page.Last().Id : null,
            hasMore);
    }
}
