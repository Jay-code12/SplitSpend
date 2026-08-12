namespace TransactionService.Application.Events;

// ── Outbound events produced by Transaction Service ───────────────────────────

public record TransactionCreatedEvent(
    Guid TransactionId,
    Guid UserId,
    string Type,
    decimal Amount,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record TransactionCompletedEvent(
    Guid TransactionId,
    Guid UserId,
    string Type,
    decimal Amount,
    string? DebitSource,
    decimal? BudgetDebited,
    decimal? MainDebited,
    Guid? CounterpartyUserId,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record TransactionFailedEvent(
    Guid TransactionId,
    Guid UserId,
    string Type,
    decimal Amount,
    string Reason,
    string IdempotencyKey,
    DateTime OccurredAt
);

// ── Inbound events consumed by Transaction Service ────────────────────────────

// From Vendor Pay Service
public record VendorPaymentApprovedEvent(
    Guid PaymentRequestId,
    Guid PayerUserId,
    Guid RequesterUserId,
    decimal Amount,
    string IdempotencyKey,
    DateTime OccurredAt
);

// From Wallet Service
public record WalletBudgetDebitedEvent(
    Guid WalletId,
    Guid UserId,
    decimal Amount,
    decimal NewBudgetBalance,
    Guid? CounterpartyId,
    string? Reference,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record WalletMainDebitedEvent(
    Guid WalletId,
    Guid UserId,
    decimal Amount,
    decimal NewMainBalance,
    Guid? CounterpartyId,
    string? Reference,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record WalletCreditedEvent(
    Guid WalletId,
    Guid UserId,
    decimal Amount,
    decimal NewMainBalance,
    decimal NewBudgetBalance,
    Guid? CounterpartyId,
    string? Reference,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record WalletInsufficientFundsEvent(
    Guid WalletId,
    Guid UserId,
    decimal AttemptedAmount,
    decimal MainBalance,
    decimal BudgetBalance,
    string IdempotencyKey,
    DateTime OccurredAt
);

// From Payment Service
public record PaymentSuccessfulEvent(
    Guid PaymentLogId,
    Guid UserId,
    decimal Amount,
    string PaystackReference,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record PaymentFailedEvent(
    Guid PaymentLogId,
    Guid UserId,
    decimal Amount,
    string? PaystackReference,
    string Reason,
    string IdempotencyKey,
    DateTime OccurredAt
);

// From Transfer Service
public record TransferCreatedEvent(
    Guid TransferId,
    Guid UserId,
    decimal Amount,
    string RecipientAccountNumber,
    string RecipientBankName,
    string RecipientAccountName,
    string PaystackReference,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record TransferCompletedEvent(
    Guid TransferId,
    Guid UserId,
    decimal Amount,
    string RecipientAccountNumber,
    string RecipientBankName,
    string RecipientAccountName,
    string PaystackTransferCode,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record TransferFailedEvent(
    Guid TransferId,
    Guid UserId,
    decimal Amount,
    string Reason,
    string IdempotencyKey,
    DateTime OccurredAt
);

// ── Topic constants ───────────────────────────────────────────────────────────

public static class KafkaTopics
{
    // Produced by Transaction Service
    public const string TransactionCreated   = "transaction.created";
    public const string TransactionCompleted = "transaction.completed";
    public const string TransactionFailed    = "transaction.failed";

    // Consumed — from Vendor Pay Service
    public const string VendorPaymentApproved = "vendor.payment.approved";

    // Consumed — from Wallet Service
    public const string WalletBudgetDebited    = "wallet.budget.debited";
    public const string WalletMainDebited      = "wallet.main.debited";
    public const string WalletCredited         = "wallet.credited";
    public const string WalletInsufficientFunds = "wallet.insufficient_funds";

    // Consumed — from Payment Service
    public const string PaymentSuccessful = "payment.successful";
    public const string PaymentFailed     = "payment.failed";

    // Consumed — from Transfer Service
    public const string TransferCreated   = "transfer.created";
    public const string TransferCompleted = "transfer.completed";
    public const string TransferFailed    = "transfer.failed";
}
