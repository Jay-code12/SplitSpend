using TransferService.Domain.Enums;

namespace TransferService.Domain.Entities;

/// <summary>
/// Core aggregate for an outbound bank transfer from Main Balance to an external Nigerian bank.
/// Owns the full lifecycle: Pending → Processing → Completed/Failed → Reversed.
///
/// Flow:
///   1. Created (Pending) — wallet pre-debit requested
///   2. wallet.main.transfer.initiated consumed → Processing — Paystack payout initiated
///   3a. Paystack webhook success → Completed
///   3b. Paystack webhook failure OR 24h timeout → Failed → Reversed (wallet credited back)
/// </summary>
public class BankTransfer
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    // Recipient bank details
    public string RecipientAccountNumber { get; private set; } = string.Empty;
    public string RecipientBankCode { get; private set; } = string.Empty;
    public string RecipientBankName { get; private set; } = string.Empty;
    public string RecipientAccountName { get; private set; } = string.Empty;   // Resolved via Paystack lookup

    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "NGN";

    public TransferStatus Status { get; private set; }

    // Paystack identifiers
    public string? PaystackTransferCode { get; private set; }   // Returned when payout is initiated
    public string? PaystackReference { get; private set; }      // Our reference sent to Paystack
    public string? PaystackWebhookData { get; private set; }    // Raw webhook payload for audit

    // Idempotency
    public string IdempotencyKey { get; private set; } = string.Empty;

    // Audit
    public string? FailureReason { get; private set; }
    public DateTime? ProcessingStartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? FailedAt { get; private set; }
    public DateTime? ReversedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private BankTransfer() { }

    public static BankTransfer Create(
        Guid userId,
        string accountNumber,
        string bankCode,
        string bankName,
        string accountName,
        decimal amount,
        string idempotencyKey)
    {
        if (amount <= 0)
            throw new TransferDomainException("Transfer amount must be positive.");
        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new TransferDomainException("Account number is required.");
        if (string.IsNullOrWhiteSpace(bankCode))
            throw new TransferDomainException("Bank code is required.");
        if (string.IsNullOrWhiteSpace(accountName))
            throw new TransferDomainException("Account name is required.");

        return new BankTransfer
        {
            Id                    = Guid.NewGuid(),
            UserId                = userId,
            RecipientAccountNumber = accountNumber,
            RecipientBankCode     = bankCode,
            RecipientBankName     = bankName,
            RecipientAccountName  = accountName,
            Amount                = amount,
            Currency              = "NGN",
            Status                = TransferStatus.Pending,
            PaystackReference     = $"SS-{Guid.NewGuid():N}",  // Unique reference for Paystack
            IdempotencyKey        = idempotencyKey,
            CreatedAt             = DateTime.UtcNow,
            UpdatedAt             = DateTime.UtcNow
        };
    }

    // ── State transitions ────────────────────────────────────────────────────

    /// <summary>
    /// Wallet confirmed the pre-debit. Safe to call Paystack payout API now.
    /// </summary>
    public void MarkProcessing(string paystackTransferCode)
    {
        if (Status != TransferStatus.Pending)
            throw new TransferDomainException($"Cannot mark Processing from {Status} state.");

        Status                = TransferStatus.Processing;
        PaystackTransferCode  = paystackTransferCode;
        ProcessingStartedAt   = DateTime.UtcNow;
        UpdatedAt             = DateTime.UtcNow;
    }

    /// <summary>
    /// Called after wallet pre-debit is confirmed but before we have Paystack transfer code.
    /// Used when transition from Pending to Processing happens in two steps.
    /// </summary>
    public void MarkProcessingInitiated()
    {
        if (Status != TransferStatus.Pending)
            throw new TransferDomainException($"Cannot initiate processing from {Status} state.");

        Status              = TransferStatus.Processing;
        ProcessingStartedAt = DateTime.UtcNow;
        UpdatedAt           = DateTime.UtcNow;
    }

    public void SetPaystackTransferCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new TransferDomainException("Paystack transfer code cannot be empty.");
        PaystackTransferCode = code;
        UpdatedAt            = DateTime.UtcNow;
    }

    /// <summary>
    /// Paystack webhook confirmed successful bank delivery.
    /// </summary>
    public void MarkCompleted(string webhookData)
    {
        if (Status != TransferStatus.Processing)
            throw new TransferDomainException($"Cannot complete a transfer in {Status} state.");

        Status             = TransferStatus.Completed;
        PaystackWebhookData = webhookData;
        CompletedAt        = DateTime.UtcNow;
        UpdatedAt          = DateTime.UtcNow;
    }

    /// <summary>
    /// Paystack rejected, timed out, or 24h auto-timeout hit.
    /// Wallet reversal will be triggered after this transition.
    /// </summary>
    public void MarkFailed(string reason, string? webhookData = null)
    {
        if (Status != TransferStatus.Processing && Status != TransferStatus.Pending)
            throw new TransferDomainException($"Cannot fail a transfer in {Status} state.");

        Status              = TransferStatus.Failed;
        FailureReason       = reason;
        PaystackWebhookData = webhookData;
        FailedAt            = DateTime.UtcNow;
        UpdatedAt           = DateTime.UtcNow;
    }

    /// <summary>
    /// Wallet confirmed the pre-debit has been reversed after failure.
    /// </summary>
    public void MarkReversed()
    {
        if (Status != TransferStatus.Failed)
            throw new TransferDomainException($"Cannot reverse a transfer in {Status} state.");

        Status     = TransferStatus.Reversed;
        ReversedAt = DateTime.UtcNow;
        UpdatedAt  = DateTime.UtcNow;
    }

    /// <summary>
    /// True if transfer has been Processing for more than 24 hours with no webhook.
    /// Triggers auto-reversal.
    /// </summary>
    public bool IsTimedOut =>
        Status == TransferStatus.Processing &&
        ProcessingStartedAt.HasValue &&
        DateTime.UtcNow - ProcessingStartedAt.Value > TimeSpan.FromHours(24);
}
