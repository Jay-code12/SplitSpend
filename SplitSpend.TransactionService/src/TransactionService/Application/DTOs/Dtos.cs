using TransactionService.Domain.Enums;

namespace TransactionService.Application.DTOs;

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record TransactionResponse(
    Guid Id,
    Guid UserId,
    string Type,
    string Status,
    decimal Amount,
    string Currency,
    string DebitSource,
    decimal? BudgetDebited,
    decimal? MainDebited,
    Guid? CounterpartyUserId,
    string? PaystackReference,
    string? ExternalTransferId,
    string? FailureReason,
    DateTime? ProcessingStartedAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    DateTime CreatedAt
);

public record PagedTransactionResponse(
    List<TransactionResponse> Transactions,
    int TotalCount,
    Guid? NextCursorId,
    bool HasMore
);

// ── Query parameters ──────────────────────────────────────────────────────────

public record TransactionQuery(
    Guid UserId,
    string? Type = null,           // "Deposit" | "InPlatformPayment" | "ExternalTransfer"
    string? Status = null,         // "Pending" | "Processing" | "Completed" | "Failed"
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    Guid? CursorId = null,
    int PageSize = 20
);
