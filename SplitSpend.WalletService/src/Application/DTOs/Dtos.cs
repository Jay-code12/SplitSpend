using WalletService.Domain.Enums;

namespace WalletService.Application.DTOs;

// ── Request DTOs ────────────────────────────────────────────────────────────

public record CreditRequest(
    Guid UserId,
    decimal Amount,
    BalanceType TargetBalance,
    string IdempotencyKey,
    Guid? CounterpartyId = null,
    string? Reference = null,
    string? Description = null
);

public record DebitRequest(
    Guid UserId,
    decimal Amount,
    string IdempotencyKey,
    Guid? CounterpartyId = null,
    string? Reference = null,
    string? Description = null
);

public record InPlatformPayRequest(
    Guid PayerUserId,
    Guid RecipientUserId,
    decimal Amount,
    string IdempotencyKey,
    string? Reference = null
);

public record InternalTransferRequest(
    Guid UserId,
    decimal Amount,
    string Direction,  // "MainToBudget" | "BudgetToMain"
    string IdempotencyKey,
    string? Description = null
);

public record LedgerQueryRequest(
    Guid UserId,
    string? Type = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    Guid? CursorId = null,
    int PageSize = 20
);

// ── Response DTOs ───────────────────────────────────────────────────────────

public record WalletBalanceResponse(
    Guid WalletId,
    Guid UserId,
    decimal MainBalance,
    decimal BudgetBalance,
    string Currency,
    string Status
);

public record CreditResponse(
    Guid LedgerId,
    decimal AmountCredited,
    decimal NewMainBalance,
    decimal NewBudgetBalance,
    string IdempotencyKey
);

public record DebitResponse(
    Guid LedgerId,
    decimal TotalDebited,
    decimal BudgetDebited,
    decimal MainDebited,
    decimal NewMainBalance,
    decimal NewBudgetBalance,
    string IdempotencyKey
);

public record InPlatformPayResponse(
    Guid PayerLedgerId,
    Guid RecipientLedgerId,
    decimal Amount,
    decimal PayerBudgetDebited,
    decimal PayerMainDebited,
    decimal PayerNewMainBalance,
    decimal PayerNewBudgetBalance,
    decimal RecipientNewMainBalance,
    string IdempotencyKey
);

public record InternalTransferResponse(
    Guid LedgerId,
    string Direction,
    decimal Amount,
    decimal NewMainBalance,
    decimal NewBudgetBalance,
    bool Success,
    string? FailureReason = null
);

public record LedgerEntryResponse(
    Guid Id,
    string EntryType,
    string? DebitSource,
    decimal Amount,
    string Currency,
    decimal MainBalanceBefore,
    decimal BudgetBalanceBefore,
    decimal MainBalanceAfter,
    decimal BudgetBalanceAfter,
    Guid? CounterpartyId,
    string? TransactionReference,
    string? Description,
    DateTime CreatedAt
);

public record PagedLedgerResponse(
    List<LedgerEntryResponse> Entries,
    Guid? NextCursorId,
    bool HasMore
);
