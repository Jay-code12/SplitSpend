namespace TransferService.Application.Events;

// ── Outbound events produced by Transfer Service ─────────────────────────────

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

// ── Inbound events consumed by Transfer Service ───────────────────────────────

public record WalletMainTransferInitiatedEvent(
    Guid WalletId,
    Guid UserId,
    decimal Amount,
    decimal NewMainBalance,
    string TransferReference,      // Matches our PaystackReference
    string IdempotencyKey,
    DateTime OccurredAt
);

// ── Kafka topic constants ─────────────────────────────────────────────────────

public static class KafkaTopics
{
    // Produced by Transfer Service
    public const string TransferCreated   = "transfer.created";
    public const string TransferCompleted = "transfer.completed";
    public const string TransferFailed    = "transfer.failed";

    // Consumed by Transfer Service
    public const string WalletMainTransferInitiated = "wallet.main.transfer.initiated";
}
