using TransferService.Application.DTOs;
using TransferService.Application.Events;
using TransferService.Application.Interfaces;
using TransferService.Domain.Entities;
using TransferService.Domain.Enums;
using TransferService.Infrastructure.Data;

namespace TransferService.Application.Services;

/// <summary>
/// Orchestrates the full external bank transfer lifecycle.
///
/// Flow:
///   InitiateAsync        → Creates BankTransfer (Pending), emits transfer.created
///                           Wallet Service pre-debits Main Balance
///                           Wallet emits wallet.main.transfer.initiated
///   OnWalletPreDebitAsync → Moves to Processing, calls Paystack Transfers API
///   HandleWebhookAsync   → On success: Completed. On failure: Failed + emit transfer.failed
///                           Wallet Service reverses the pre-debit on transfer.failed
///   RunTimeoutCheckAsync → After 24h with no webhook: force Failed + reversal
/// </summary>
public class TransferApplicationService
{
    private readonly ITransferRepository _transfers;
    private readonly IBeneficiaryRepository _beneficiaries;
    private readonly IIdempotencyRepository _idempotency;
    private readonly IPaystackClient _paystack;
    private readonly IWalletServiceClient _walletClient;
    private readonly IKafkaPublisher _kafka;
    private readonly TransferDbContext _db;
    private readonly ILogger<TransferApplicationService> _log;

    public TransferApplicationService(
        ITransferRepository transfers,
        IBeneficiaryRepository beneficiaries,
        IIdempotencyRepository idempotency,
        IPaystackClient paystack,
        IWalletServiceClient walletClient,
        IKafkaPublisher kafka,
        TransferDbContext db,
        ILogger<TransferApplicationService> log)
    {
        _transfers    = transfers;
        _beneficiaries = beneficiaries;
        _idempotency  = idempotency;
        _paystack     = paystack;
        _walletClient = walletClient;
        _kafka        = kafka;
        _db           = db;
        _log          = log;
    }

    // ── INITIATE TRANSFER ────────────────────────────────────────────────────

    /// <summary>
    /// Entry point for a user-initiated transfer. PIN already verified by API Gateway.
    ///
    /// 1. Idempotency check
    /// 2. Pre-flight: verify account name via Paystack (so user sees who they're paying)
    /// 3. Pre-flight: verify Main Balance >= amount via Wallet Service
    /// 4. Create BankTransfer (Pending) + cache beneficiary
    /// 5. Emit transfer.created → Transaction Service opens a transaction record
    /// 6. Wallet Service listens to transfer.created and pre-debits MainBalance,
    ///    then emits wallet.main.transfer.initiated
    /// 7. OnWalletPreDebitAsync is triggered by the Kafka consumer
    /// </summary>
    public async Task<InitiateTransferResponse> InitiateAsync(
        InitiateTransferRequest req, CancellationToken ct)
    {
        if (await _idempotency.ExistsAsync(req.IdempotencyKey, ct))
            throw new DuplicateIdempotencyKeyException(req.IdempotencyKey);

        // Step 1: Resolve account name — gives user confirmation of recipient
        VerifyAccountResponse accountInfo;
        try
        {
            accountInfo = await _paystack.VerifyAccountAsync(req.AccountNumber, req.BankCode, ct);
        }
        catch (PaystackApiException ex)
        {
            _log.LogWarning("Account verification failed for {Account}/{Bank}: {Error}",
                req.AccountNumber, req.BankCode, ex.Message);
            throw new TransferDomainException(
                $"Could not verify account: {ex.Message}");
        }

        // Step 2: Pre-flight balance check
        var mainBalance = await _walletClient.GetMainBalanceAsync(req.UserId, ct);
        if (mainBalance < req.Amount)
            throw new TransferDomainException(
                $"Insufficient Main Balance. Available: ₦{mainBalance:N2}, Required: ₦{req.Amount:N2}");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var transfer = BankTransfer.Create(
                req.UserId,
                req.AccountNumber,
                req.BankCode,
                accountInfo.BankName,
                accountInfo.AccountName,
                req.Amount,
                req.IdempotencyKey);

            await _transfers.AddAsync(transfer, ct);

            // Cache beneficiary for future transfers
            await UpsertBeneficiaryAsync(
                req.UserId, req.AccountNumber, req.BankCode,
                accountInfo.BankName, accountInfo.AccountName, ct);

            await _idempotency.MarkAsync(req.IdempotencyKey, ct);
            await _transfers.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Emit transfer.created — Transaction Service opens a record,
            // Wallet Service pre-debits Main Balance
            await _kafka.PublishAsync(KafkaTopics.TransferCreated, new TransferCreatedEvent(
                transfer.Id, transfer.UserId, transfer.Amount,
                transfer.RecipientAccountNumber, transfer.RecipientBankName,
                transfer.RecipientAccountName, transfer.PaystackReference!,
                req.IdempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Transfer {TransferId} created (Pending): ₦{Amount} to {Account} at {Bank}",
                transfer.Id, transfer.Amount,
                transfer.RecipientAccountNumber, transfer.RecipientBankName);

            return MapToInitiateResponse(transfer);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── ON WALLET PRE-DEBIT CONFIRMED ────────────────────────────────────────

    /// <summary>
    /// Called by WalletMainTransferInitiatedConsumer when wallet.main.transfer.initiated arrives.
    /// Wallet pre-debit is confirmed — now safe to call Paystack Transfers API.
    ///
    /// If Paystack call fails immediately (HTTP error / validation), we emit transfer.failed
    /// right away so Wallet Service can reverse the pre-debit.
    /// </summary>
    public async Task OnWalletPreDebitAsync(
        string paystackReference, Guid userId, decimal amount,
        string walletIdempotencyKey, CancellationToken ct)
    {
        var processKey = walletIdempotencyKey + ":process";
        if (await _idempotency.ExistsAsync(processKey, ct))
        {
            _log.LogWarning("Duplicate wallet pre-debit event for ref {Ref}", paystackReference);
            return;
        }

        var transfer = await _transfers.GetByPaystackReferenceAsync(paystackReference, ct);
        if (transfer == null)
        {
            _log.LogError(
                "wallet.main.transfer.initiated received but no transfer found for ref {Ref}",
                paystackReference);
            return;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            transfer.MarkProcessingInitiated();
            await _idempotency.MarkAsync(processKey, ct);
            await _transfers.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        // Call Paystack outside the DB transaction — if it fails, we fail the transfer
        string? paystackCode = null;
        try
        {
            paystackCode = await _paystack.InitiateTransferAsync(
                transfer.RecipientAccountNumber,
                transfer.RecipientBankCode,
                transfer.RecipientAccountName,
                transfer.Amount,
                transfer.PaystackReference!,
                ct);

            // Persist the Paystack transfer code
            await using var tx2 = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                transfer.SetPaystackTransferCode(paystackCode);
                await _transfers.SaveChangesAsync(ct);
                await tx2.CommitAsync(ct);
            }
            catch
            {
                await tx2.RollbackAsync(ct);
                // Non-fatal — code is saved in Paystack; webhook will arrive regardless
                _log.LogWarning("Failed to persist Paystack transfer code {Code} for {TransferId}",
                    paystackCode, transfer.Id);
            }

            _log.LogInformation(
                "Paystack payout initiated for transfer {TransferId}: code={Code}",
                transfer.Id, paystackCode);
        }
        catch (PaystackApiException ex)
        {
            // Paystack rejected the payout immediately — fail and trigger reversal
            _log.LogError(ex,
                "Paystack payout API rejected transfer {TransferId}: {Error}",
                transfer.Id, ex.Message);

            await FailTransferAsync(transfer, ex.Message, null, ct);
        }
    }

    // ── HANDLE PAYSTACK WEBHOOK ───────────────────────────────────────────────

    /// <summary>
    /// Processes an inbound Paystack transfer webhook.
    /// Signature MUST be verified by the controller before calling this method.
    ///
    /// Supported events:
    ///   transfer.success  → MarkCompleted, emit transfer.completed
    ///   transfer.failed   → MarkFailed, emit transfer.failed (triggers wallet reversal)
    ///   transfer.reversed → MarkFailed if still Processing (Paystack-initiated reversal)
    /// </summary>
    public async Task HandleWebhookAsync(
        PaystackWebhookRequest webhook, string rawPayload, CancellationToken ct)
    {
        var webhookKey = $"webhook:{webhook.Data.Reference}:{webhook.Event}";
        if (await _idempotency.ExistsAsync(webhookKey, ct))
        {
            _log.LogInformation(
                "Duplicate webhook ignored: event={Event} ref={Ref}",
                webhook.Event, webhook.Data.Reference);
            return;
        }

        var transfer = await _transfers.GetByPaystackReferenceAsync(webhook.Data.Reference, ct);
        if (transfer == null)
        {
            _log.LogWarning(
                "Webhook received for unknown reference {Ref} — event={Event}",
                webhook.Data.Reference, webhook.Event);
            await _idempotency.MarkAsync(webhookKey, ct);
            await _idempotency.MarkAsync(webhookKey, ct); // ensure it's saved
            await _db.SaveChangesAsync(ct);
            return;
        }

        switch (webhook.Event.ToLowerInvariant())
        {
            case "transfer.success":
                await CompleteTransferAsync(transfer, rawPayload, webhookKey, ct);
                break;

            case "transfer.failed":
            case "transfer.reversed":
                await FailTransferAsync(
                    transfer,
                    $"Paystack event: {webhook.Event} — {webhook.Data.Reason}",
                    rawPayload, ct, webhookKey);
                break;

            default:
                _log.LogWarning("Unrecognised Paystack webhook event: {Event}", webhook.Event);
                break;
        }

        await _idempotency.MarkAsync(webhookKey, ct);
        await _db.SaveChangesAsync(ct);
    }

    // ── TIMEOUT CHECK (Hangfire scheduled job) ────────────────────────────────

    /// <summary>
    /// Polls all transfers that have been in Processing for > 24 hours.
    /// For each one, queries Paystack for the real status.
    /// If still unknown/pending at Paystack: force fail and trigger wallet reversal.
    /// Runs every 30 minutes via Hangfire, as specified in the MVP risk mitigations.
    /// </summary>
    public async Task RunTimeoutCheckAsync(CancellationToken ct = default)
    {
        var timedOut = await _transfers.GetTimedOutTransfersAsync(ct);

        if (timedOut.Count == 0)
        {
            _log.LogDebug("Timeout check: no timed-out transfers found");
            return;
        }

        _log.LogWarning("Timeout check: {Count} timed-out transfers found", timedOut.Count);

        foreach (var transfer in timedOut)
        {
            var timeoutKey = $"timeout:{transfer.Id}";
            if (await _idempotency.ExistsAsync(timeoutKey, ct))
                continue;

            try
            {
                // Check real status at Paystack before failing
                if (!string.IsNullOrEmpty(transfer.PaystackTransferCode))
                {
                    var paystackStatus = await _paystack.GetTransferStatusAsync(
                        transfer.PaystackTransferCode, ct);

                    if (paystackStatus == "success")
                    {
                        // Paystack succeeded but webhook was missed — complete it
                        _log.LogWarning(
                            "Transfer {TransferId} timed out locally but Paystack shows success — completing",
                            transfer.Id);
                        await CompleteTransferAsync(transfer, "{\"source\":\"timeout-recovery\"}", timeoutKey, ct);
                        continue;
                    }
                }

                _log.LogWarning(
                    "Transfer {TransferId} timed out after 24h — forcing failure and reversal",
                    transfer.Id);

                await FailTransferAsync(transfer, "Transfer timed out after 24 hours with no webhook confirmation.",
                    null, ct, timeoutKey);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Timeout check failed for transfer {TransferId}", transfer.Id);
            }
        }
    }

    // ── MANUAL VERIFY (recovery endpoint) ────────────────────────────────────

    /// <summary>
    /// Manually re-checks a transfer status from Paystack.
    /// Used by support or the user's "re-check" button when webhook was missed.
    /// </summary>
    public async Task<TransferDetailResponse> VerifyTransferAsync(
        Guid transferId, Guid userId, CancellationToken ct)
    {
        var transfer = await _transfers.GetByIdRequiredAsync(transferId, ct);
        if (transfer.UserId != userId)
            throw new TransferNotOwnedException(transferId, userId);

        // Only check Paystack if it's in a state we can recover from
        if (transfer.Status == TransferStatus.Processing &&
            !string.IsNullOrEmpty(transfer.PaystackTransferCode))
        {
            try
            {
                var status = await _paystack.GetTransferStatusAsync(
                    transfer.PaystackTransferCode, ct);

                var verifyKey = $"verify:{transfer.Id}:{status}";
                if (!await _idempotency.ExistsAsync(verifyKey, ct))
                {
                    if (status == "success")
                        await CompleteTransferAsync(transfer, "{\"source\":\"manual-verify\"}", verifyKey, ct);
                    else if (status is "failed" or "reversed")
                        await FailTransferAsync(transfer, $"Manual verify: Paystack status={status}", null, ct, verifyKey);
                }
            }
            catch (PaystackApiException ex)
            {
                _log.LogWarning("Could not verify transfer {TransferId} at Paystack: {Error}",
                    transferId, ex.Message);
            }
        }

        return MapToDetailResponse(transfer);
    }

    // ── QUERIES ───────────────────────────────────────────────────────────────

    public async Task<List<TransferDetailResponse>> GetUserTransfersAsync(
        Guid userId, CancellationToken ct)
    {
        var transfers = await _transfers.GetByUserIdAsync(userId, ct);
        return transfers.Select(MapToDetailResponse).ToList();
    }

    public async Task<TransferDetailResponse> GetTransferAsync(
        Guid transferId, Guid userId, CancellationToken ct)
    {
        var transfer = await _transfers.GetByIdRequiredAsync(transferId, ct);
        if (transfer.UserId != userId)
            throw new TransferNotOwnedException(transferId, userId);
        return MapToDetailResponse(transfer);
    }

    // ── PRIVATE HELPERS ───────────────────────────────────────────────────────

    private async Task CompleteTransferAsync(
        BankTransfer transfer, string webhookData, string idempotencyKey, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            transfer.MarkCompleted(webhookData);
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _transfers.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.TransferCompleted, new TransferCompletedEvent(
                transfer.Id, transfer.UserId, transfer.Amount,
                transfer.RecipientAccountNumber, transfer.RecipientBankName,
                transfer.RecipientAccountName,
                transfer.PaystackTransferCode ?? string.Empty,
                idempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Transfer {TransferId} COMPLETED: ₦{Amount} → {Account} at {Bank}",
                transfer.Id, transfer.Amount,
                transfer.RecipientAccountNumber, transfer.RecipientBankName);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task FailTransferAsync(
        BankTransfer transfer, string reason, string? webhookData,
        CancellationToken ct, string? idempotencyKey = null)
    {
        var failKey = idempotencyKey ?? $"fail:{transfer.Id}:{DateTime.UtcNow:yyyyMMddHH}";

        if (await _idempotency.ExistsAsync(failKey, ct))
            return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            transfer.MarkFailed(reason, webhookData);
            await _idempotency.MarkAsync(failKey, ct);
            await _transfers.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Emit transfer.failed → Wallet Service reverses the pre-debit
            // → Transaction Service marks the transaction Failed
            // → Notification Service alerts user
            await _kafka.PublishAsync(KafkaTopics.TransferFailed, new TransferFailedEvent(
                transfer.Id, transfer.UserId, transfer.Amount,
                reason, failKey, DateTime.UtcNow), ct);

            _log.LogWarning(
                "Transfer {TransferId} FAILED: {Reason}. Reversal triggered via Kafka.",
                transfer.Id, reason);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task UpsertBeneficiaryAsync(
        Guid userId, string accountNumber, string bankCode,
        string bankName, string accountName, CancellationToken ct)
    {
        var existing = await _beneficiaries.GetAsync(userId, accountNumber, bankCode, ct);
        if (existing != null)
        {
            existing.UpdateAccountName(accountName);
        }
        else
        {
            var beneficiary = BankBeneficiary.Create(
                userId, accountNumber, bankCode, bankName, accountName);
            await _beneficiaries.AddAsync(beneficiary, ct);
        }
    }

    // ── Mappers ───────────────────────────────────────────────────────────────

    private static InitiateTransferResponse MapToInitiateResponse(BankTransfer t) => new(
        t.Id, t.UserId,
        t.RecipientAccountNumber, t.RecipientBankName, t.RecipientAccountName,
        t.Amount, t.Status.ToString(), t.IdempotencyKey, t.CreatedAt);

    private static TransferDetailResponse MapToDetailResponse(BankTransfer t) => new(
        t.Id, t.UserId,
        t.RecipientAccountNumber, t.RecipientBankCode,
        t.RecipientBankName, t.RecipientAccountName,
        t.Amount, t.Currency, t.Status.ToString(),
        t.PaystackTransferCode, t.PaystackReference,
        t.FailureReason,
        t.ProcessingStartedAt, t.CompletedAt, t.FailedAt, t.ReversedAt,
        t.CreatedAt);
}
