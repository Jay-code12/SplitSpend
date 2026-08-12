namespace PaymentService.Application.Events;

// ── Outbound events produced by Payment Service ───────────────────────────────

public record PaymentSuccessfulEvent(
    Guid PaymentLogId,
    Guid UserId,
    decimal Amount,
    string PaystackReference,
    string? Channel,
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

// ── Topic constants ───────────────────────────────────────────────────────────

public static class KafkaTopics
{
    // Produced by Payment Service
    public const string PaymentSuccessful = "payment.successful";
    public const string PaymentFailed     = "payment.failed";

    // Payment Service produces only — it consumes nothing.
    // It is the entry point for all external money coming INTO the platform.
}
