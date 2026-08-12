using TransferService.Domain.Enums;

namespace TransferService.Application.DTOs;

// ── Request DTOs ─────────────────────────────────────────────────────────────

public record InitiateTransferRequest(
    Guid UserId,
    string AccountNumber,
    string BankCode,
    decimal Amount,
    string IdempotencyKey
    // Note: PIN is validated at the API Gateway before this service is called.
    // The Gateway enforces /api/transfers/* PIN requirement per the MVP spec.
);

public record VerifyAccountRequest(
    string AccountNumber,
    string BankCode
);

public record PaystackWebhookRequest(
    string Event,           // "transfer.success" | "transfer.failed" | "transfer.reversed"
    PaystackTransferData Data
);

public record PaystackTransferData(
    string Reference,       // Matches our PaystackReference field
    string TransferCode,
    string Status,
    string Reason,
    decimal Amount,         // Paystack returns kobo — divide by 100
    string CreatedAt,
    string UpdatedAt
);

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record InitiateTransferResponse(
    Guid TransferId,
    Guid UserId,
    string RecipientAccountNumber,
    string RecipientBankName,
    string RecipientAccountName,
    decimal Amount,
    string Status,
    string IdempotencyKey,
    DateTime CreatedAt
);

public record TransferDetailResponse(
    Guid Id,
    Guid UserId,
    string RecipientAccountNumber,
    string RecipientBankCode,
    string RecipientBankName,
    string RecipientAccountName,
    decimal Amount,
    string Currency,
    string Status,
    string? PaystackTransferCode,
    string? PaystackReference,
    string? FailureReason,
    DateTime? ProcessingStartedAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    DateTime? ReversedAt,
    DateTime CreatedAt
);

public record VerifyAccountResponse(
    string AccountNumber,
    string AccountName,
    string BankCode,
    string BankName
);

public record NigerianBank(
    string Name,
    string Code,
    string? LongCode,
    bool Active
);

public record BankListResponse(
    List<NigerianBank> Banks,
    DateTime CachedAt
);

public record WebhookAcknowledgement(
    bool Processed,
    string Message
);
