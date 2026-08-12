namespace BudgetService.Application.Events;

// ── Outbound events produced by Budget Service ───────────────────────────────

public record BudgetCreatedEvent(
    Guid BudgetId,
    Guid UserId,
    decimal TotalAmount,
    decimal DailyAmount,
    int DurationDays,
    DateTime StartDate,
    DateTime EndDate,
    string Source,          // "Self" | "Gift"
    Guid? GiftSenderId,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record BudgetActivatedEvent(
    Guid BudgetId,
    Guid UserId,
    decimal TotalAmount,
    decimal DailyAmount,
    DateTime StartDate,
    DateTime EndDate,
    string IdempotencyKey,
    DateTime OccurredAt
);

public record BudgetDailyReleasedEvent(
    Guid UserId,
    DateTime Date,
    decimal TotalDailyAmount,
    List<BudgetDailySplit> Splits,   // Per-budget breakdown
    string IdempotencyKey,
    DateTime OccurredAt
);

public record BudgetDailySplit(
    Guid BudgetId,
    decimal Amount
);

public record BudgetDailyExpiredEvent(
    Guid UserId,
    DateTime Date,
    decimal UnusedAmount,           // Amount being returned to Main Balance
    string IdempotencyKey,
    DateTime OccurredAt
);

public record BudgetCompletedEvent(
    Guid BudgetId,
    Guid UserId,
    decimal TotalAmount,
    string Reason,   // "FullyConsumed" | "EndDateReached"
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

public record GiftSentEvent(
    Guid GiftId,
    Guid SenderUserId,
    Guid ReceiverUserId,
    decimal Amount,
    int DurationDays,
    string? Message,
    string IdempotencyKey,
    DateTime OccurredAt
);

// ── Inbound event contracts (consumed from other services) ────────────────────

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

// ── Kafka topic constants ─────────────────────────────────────────────────────

public static class KafkaTopics
{
    // Produced by Budget Service
    public const string BudgetCreated        = "budget.created";
    public const string BudgetActivated      = "budget.activated";
    public const string BudgetDailyReleased  = "budget.daily.released";
    public const string BudgetDailyExpired   = "budget.daily.expired";
    public const string BudgetCompleted      = "budget.completed";
    public const string BudgetCancelled      = "budget.cancelled";
    public const string GiftSent             = "gift.sent";

    // Consumed by Budget Service
    public const string WalletBudgetTransferCompleted = "wallet.budget.transfer.completed";
    public const string WalletBudgetTransferFailed    = "wallet.budget.transfer.failed";
    public const string WalletBudgetDebited           = "wallet.budget.debited";
}
