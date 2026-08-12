using BudgetService.Domain.Enums;

namespace BudgetService.Application.DTOs;

// ── Request DTOs ─────────────────────────────────────────────────────────────

public record CreateBudgetRequest(
    Guid UserId,
    decimal TotalAmount,
    int DurationDays,
    DateTime StartDate,
    string IdempotencyKey
);

public record SendGiftRequest(
    Guid SenderUserId,
    Guid ReceiverUserId,
    decimal Amount,
    int DurationDays,
    string IdempotencyKey,
    string? Message = null
);

public record CancelBudgetRequest(
    Guid BudgetId,
    Guid UserId   // Must match budget owner; enforced in service
);

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record BudgetResponse(
    Guid Id,
    Guid UserId,
    decimal TotalAmount,
    decimal DailyAmount,
    decimal RemainingTotal,
    int DurationDays,
    DateTime StartDate,
    DateTime EndDate,
    string Status,
    string Source,
    Guid? GiftSenderId,
    DateTime CreatedAt
);

public record DailySummaryResponse(
    Guid UserId,
    DateTime Date,
    decimal TotalAllocated,
    decimal TotalSpent,
    decimal Remaining,
    List<ActiveBudgetDailySplit> BudgetBreakdown
);

public record ActiveBudgetDailySplit(
    Guid BudgetId,
    decimal DailyAllocation,
    decimal SpentToday,
    decimal RemainingToday
);

public record GiftBudgetResponse(
    Guid Id,
    Guid SenderUserId,
    Guid ReceiverUserId,
    decimal Amount,
    int DurationDays,
    string Status,
    Guid? ResultingBudgetId,
    string? Message,
    DateTime CreatedAt
);

public record CronJobResult(
    string JobName,
    DateTime RunAt,
    int UsersProcessed,
    int SuccessCount,
    int FailureCount,
    List<string> Errors
);
