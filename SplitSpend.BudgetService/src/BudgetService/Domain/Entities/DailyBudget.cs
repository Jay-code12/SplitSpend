namespace BudgetService.Domain.Entities;

/// <summary>
/// Tracks the aggregate daily budget position for a user on a given day.
/// Refreshed each morning by the CRON job (budget.daily.released).
/// Decremented in real-time as wallet.budget.debited events arrive.
/// </summary>
public class UserTotalDailyBudget
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime Date { get; private set; }          // UTC date (date-only, no time)

    public decimal TotalAllocated { get; private set; } // Sum of DailyAmount across all active budgets
    public decimal TotalSpent { get; private set; }     // Running total debited from budget balance today
    public decimal Remaining => TotalAllocated - TotalSpent;

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private UserTotalDailyBudget() { }

    public static UserTotalDailyBudget Create(Guid userId, DateTime date, decimal totalAllocated)
    {
        if (totalAllocated < 0)
            throw new BudgetDomainException("Allocated amount cannot be negative.");

        return new UserTotalDailyBudget
        {
            Id             = Guid.NewGuid(),
            UserId         = userId,
            Date           = date.Date,
            TotalAllocated = totalAllocated,
            TotalSpent     = 0,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow
        };
    }

    public void RecordSpend(decimal amount)
    {
        if (amount <= 0)
            throw new BudgetDomainException("Spend amount must be positive.");
        TotalSpent += amount;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddAllocation(decimal amount)
    {
        if (amount <= 0)
            throw new BudgetDomainException("Allocation amount must be positive.");
        TotalAllocated += amount;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Per-budget daily allocation record. One row per budget per day.
/// Used for FIFO distribution when wallet.budget.debited is consumed.
/// </summary>
public class DailyBudgetRecord
{
    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime Date { get; private set; }

    public decimal AllocatedAmount { get; private set; }
    public decimal SpentAmount { get; private set; }
    public decimal UnusedAmount => AllocatedAmount - SpentAmount;

    public bool IsExpired { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public Budget Budget { get; private set; } = null!;

    private DailyBudgetRecord() { }

    public static DailyBudgetRecord Create(Guid budgetId, Guid userId, DateTime date, decimal allocatedAmount)
    {
        return new DailyBudgetRecord
        {
            Id              = Guid.NewGuid(),
            BudgetId        = budgetId,
            UserId          = userId,
            Date            = date.Date,
            AllocatedAmount = allocatedAmount,
            SpentAmount     = 0,
            IsExpired       = false,
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow
        };
    }

    public decimal RecordSpend(decimal amount)
    {
        var consumable = Math.Min(amount, UnusedAmount);
        SpentAmount  += consumable;
        UpdatedAt     = DateTime.UtcNow;
        return consumable;
    }

    public void MarkExpired()
    {
        IsExpired = true;
        UpdatedAt = DateTime.UtcNow;
    }
}
