namespace WalletService.Application.Events;

// ── Outbound events produced by Wallet Service ──────────────────────────────

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

public record WalletBudgetTransferCompletedEvent(
    Guid WalletId,
    Guid UserId,
    decimal Amount,
    decimal NewMainBalance,
    decimal NewBudgetBalance,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record WalletBudgetTransferFailedEvent(
    Guid WalletId,
    Guid UserId,
    decimal Amount,
    string Reason,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record WalletMainTransferInitiatedEvent(
    Guid WalletId,
    Guid UserId,
    decimal Amount,
    decimal NewMainBalance,
    string TransferReference,
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

// ── Inbound event contracts (consumed from other services) ──────────────────

public record VendorPaymentApprovedEvent(
    Guid PaymentRequestId,
    Guid PayerUserId,
    Guid RequesterUserId,
    decimal Amount,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record PaymentSuccessfulEvent(
    Guid PaymentLogId,
    Guid UserId,
    decimal Amount,
    string PaystackReference,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record BudgetCreatedEvent(
    Guid BudgetId,
    Guid UserId,
    decimal TotalAmount,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record BudgetDailyExpiredEvent(
    Guid BudgetId,
    Guid UserId,
    decimal UnusedAmount,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record GiftSentEvent(
    Guid GiftId,
    Guid SenderUserId,
    Guid ReceiverUserId,
    decimal Amount,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record BudgetCancelledEvent(
    Guid BudgetId,
    Guid UserId,
    decimal RemainingAmount,
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
