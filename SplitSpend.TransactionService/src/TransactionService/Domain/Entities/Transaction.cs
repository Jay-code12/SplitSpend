using TransactionService.Domain.Enums;

namespace TransactionService.Domain.Entities;

/// <summary>
/// Lifecycle coordinator for every money movement on the platform.
/// One Transaction record per financial operation, regardless of type.
///
/// The Transaction Service does NOT move money — it observes Kafka events
/// from Wallet Service, Payment Service, and Transfer Service and advances
/// this state machine accordingly.
///
/// State machine:
///   Deposit:          payment.successful → Pending → wallet.credited → Completed
///                     payment.failed → Failed
///
///   InPlatformPayment: vendor.payment.approved → Pending
///                      wallet.budget.debited / wallet.main.debited → Processing
///                      wallet.credited (recipient) → Completed
///                      wallet.insufficient_funds → Failed
///
///   ExternalTransfer: transfer.created → Pending
///                     transfer.completed → Completed
///                     transfer.failed → Failed
/// </summary>
public class Transaction
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    public TransactionType Type { get; private set; }
    public TransactionStatus Status { get; private set; }

    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "NGN";

    // Debit tracking — records which balance was used for spends
    public DebitSource DebitSource { get; private set; }
    public decimal? BudgetDebited { get; private set; }  // Amount deducted from BudgetBalance
    public decimal? MainDebited { get; private set; }    // Amount deducted from MainBalance

    // Counterparty — populated for in-platform payments
    public Guid? CounterpartyUserId { get; private set; }  // Recipient (payment) or Payer (receipt)

    // External references
    public string? PaystackReference { get; private set; }  // Deposit or transfer ref
    public string? ExternalTransferId { get; private set; } // BankTransfer.Id for ExternalTransfer type

    // Idempotency — prevents duplicate records from duplicate Kafka events
    public string IdempotencyKey { get; private set; } = string.Empty;

    // Audit
    public string? FailureReason { get; private set; }
    public DateTime? ProcessingStartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? FailedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Transaction() { }

    // ── Factory methods — one per transaction type ────────────────────────────

    /// <summary>
    /// Opens a Deposit transaction when payment.successful is consumed.
    /// Starts Pending; completes when wallet.credited is consumed.
    /// </summary>
    public static Transaction CreateDeposit(
        Guid userId, decimal amount, string paystackReference, string idempotencyKey)
    {
        ValidateAmount(amount);
        return new Transaction
        {
            Id                = Guid.NewGuid(),
            UserId            = userId,
            Type              = TransactionType.Deposit,
            Status            = TransactionStatus.Pending,
            Amount            = amount,
            DebitSource       = DebitSource.None,
            PaystackReference = paystackReference,
            IdempotencyKey    = idempotencyKey,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Opens an InPlatformPayment transaction when vendor.payment.approved is consumed.
    /// Starts Pending; Processing when wallet debits; Completed when recipient credited.
    /// </summary>
    public static Transaction CreateInPlatformPayment(
        Guid payerUserId, Guid recipientUserId, decimal amount, string idempotencyKey)
    {
        ValidateAmount(amount);
        return new Transaction
        {
            Id                  = Guid.NewGuid(),
            UserId              = payerUserId,
            Type                = TransactionType.InPlatformPayment,
            Status              = TransactionStatus.Pending,
            Amount              = amount,
            DebitSource         = DebitSource.None,   // Updated when debit event arrives
            CounterpartyUserId  = recipientUserId,
            IdempotencyKey      = idempotencyKey,
            CreatedAt           = DateTime.UtcNow,
            UpdatedAt           = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Opens an ExternalTransfer transaction when transfer.created is consumed.
    /// Starts Pending; Completed/Failed when transfer lifecycle events arrive.
    /// </summary>
    public static Transaction CreateExternalTransfer(
        Guid userId, decimal amount, string externalTransferId, string idempotencyKey)
    {
        ValidateAmount(amount);
        return new Transaction
        {
            Id                = Guid.NewGuid(),
            UserId            = userId,
            Type              = TransactionType.ExternalTransfer,
            Status            = TransactionStatus.Pending,
            Amount            = amount,
            DebitSource       = DebitSource.Main,  // External transfers always use Main Balance
            ExternalTransferId = externalTransferId,
            IdempotencyKey    = idempotencyKey,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow
        };
    }

    // ── State transitions ─────────────────────────────────────────────────────

    /// <summary>
    /// Records payer debit details and advances to Processing.
    /// Called when wallet.budget.debited or wallet.main.debited is consumed.
    /// </summary>
    public void RecordDebitAndMarkProcessing(
        DebitSource source, decimal budgetDebited, decimal mainDebited)
    {
        if (Status != TransactionStatus.Pending && Status != TransactionStatus.Processing)
            throw new TransactionDomainException(
                $"Cannot record debit on transaction in {Status} state.");

        DebitSource          = source;
        BudgetDebited        = budgetDebited > 0 ? budgetDebited : null;
        MainDebited          = mainDebited > 0 ? mainDebited : null;
        Status               = TransactionStatus.Processing;
        ProcessingStartedAt  = DateTime.UtcNow;
        UpdatedAt            = DateTime.UtcNow;
    }

    /// <summary>
    /// Advances directly from Pending to Processing without debit detail.
    /// Used for deposit transactions that don't have a debit phase.
    /// </summary>
    public void MarkProcessing()
    {
        if (Status != TransactionStatus.Pending)
            throw new TransactionDomainException(
                $"Cannot mark Processing from {Status} state.");

        Status              = TransactionStatus.Processing;
        ProcessingStartedAt = DateTime.UtcNow;
        UpdatedAt           = DateTime.UtcNow;
    }

    /// <summary>
    /// Full lifecycle success. Called when:
    /// - Deposit: wallet.credited consumed
    /// - InPlatformPayment: wallet.credited (recipient) consumed
    /// - ExternalTransfer: transfer.completed consumed
    /// </summary>
    public void Complete()
    {
        if (Status == TransactionStatus.Completed)
            return; // Idempotent — already completed

        if (Status == TransactionStatus.Failed)
            throw new TransactionDomainException("Cannot complete a failed transaction.");

        Status      = TransactionStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        UpdatedAt   = DateTime.UtcNow;
    }

    /// <summary>
    /// Any step in the chain failed. Called when:
    /// - Deposit: payment.failed consumed
    /// - InPlatformPayment: wallet.insufficient_funds consumed
    /// - ExternalTransfer: transfer.failed consumed
    /// </summary>
    public void Fail(string reason)
    {
        if (Status == TransactionStatus.Failed)
            return; // Idempotent

        if (Status == TransactionStatus.Completed)
            throw new TransactionDomainException("Cannot fail a completed transaction.");

        Status        = TransactionStatus.Failed;
        FailureReason = reason;
        FailedAt      = DateTime.UtcNow;
        UpdatedAt     = DateTime.UtcNow;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0)
            throw new TransactionDomainException("Transaction amount must be positive.");
    }

    /// <summary>
    /// True if this transaction is the payer side of an in-platform payment
    /// and we're waiting for the recipient credit event to close it.
    /// </summary>
    public bool IsAwaitingRecipientCredit =>
        Type == TransactionType.InPlatformPayment &&
        Status == TransactionStatus.Processing;
}
