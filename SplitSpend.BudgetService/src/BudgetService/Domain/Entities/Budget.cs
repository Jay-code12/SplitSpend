using BudgetService.Domain.Enums;

namespace BudgetService.Domain.Entities;

/// <summary>
/// Core budget aggregate. Owns all state transitions and invariants.
/// Does NOT move money — it orchestrates via events consumed/produced by the application layer.
/// </summary>
public class Budget
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    public decimal TotalAmount { get; private set; }
    public decimal DailyAmount { get; private set; }       // TotalAmount / DurationDays
    public decimal RemainingTotal { get; private set; }    // Decremented as spending is tracked

    public int DurationDays { get; private set; }
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }

    public BudgetStatus Status { get; private set; }
    public BudgetSource Source { get; private set; }

    public Guid? GiftSenderId { get; private set; }   // Populated when Source = Gift

    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public IReadOnlyCollection<DailyBudgetRecord> DailyRecords => _dailyRecords.AsReadOnly();
    private readonly List<DailyBudgetRecord> _dailyRecords = new();

    private Budget() { }

    /// <summary>
    /// Creates a budget in Pending state.
    /// Wallet must confirm the fund transfer before this becomes Active.
    /// </summary>
    public static Budget Create(
        Guid userId,
        decimal totalAmount,
        int durationDays,
        DateTime startDate,
        string idempotencyKey,
        BudgetSource source = BudgetSource.Self,
        Guid? giftSenderId = null)
    {
        if (totalAmount <= 0)
            throw new BudgetDomainException("Total amount must be positive.");
        if (durationDays <= 0)
            throw new BudgetDomainException("Duration must be at least 1 day.");
        if (source == BudgetSource.Gift && giftSenderId == null)
            throw new BudgetDomainException("Gift budgets require a sender ID.");

        var dailyAmount = Math.Round(totalAmount / durationDays, 2, MidpointRounding.AwayFromZero);

        return new Budget
        {
            Id             = Guid.NewGuid(),
            UserId         = userId,
            TotalAmount    = totalAmount,
            DailyAmount    = dailyAmount,
            RemainingTotal = totalAmount,
            DurationDays   = durationDays,
            StartDate      = startDate.Date,
            EndDate        = startDate.Date.AddDays(durationDays - 1),
            Status         = BudgetStatus.Pending,
            Source         = source,
            GiftSenderId   = giftSenderId,
            IdempotencyKey = idempotencyKey,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow
        };
    }

    // ── State transitions ────────────────────────────────────────────────────

    public void Activate()
    {
        if (Status != BudgetStatus.Pending)
            throw new BudgetDomainException($"Cannot activate a budget in {Status} state.");
        Status    = BudgetStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        if (Status != BudgetStatus.Pending)
            throw new BudgetDomainException($"Cannot fail a budget in {Status} state.");
        Status    = BudgetStatus.Failed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Cancel()
    {
        if (Status != BudgetStatus.Active && Status != BudgetStatus.Pending)
            throw new BudgetDomainException($"Cannot cancel a budget in {Status} state.");
        if (Source == BudgetSource.Gift)
            throw new BudgetDomainException("Gift budgets cannot be cancelled.");
        Status    = BudgetStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Complete()
    {
        if (Status != BudgetStatus.Active)
            throw new BudgetDomainException($"Cannot complete a budget in {Status} state.");
        Status    = BudgetStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }

    // ── Spend tracking ───────────────────────────────────────────────────────

    /// <summary>
    /// Deducts spend from RemainingTotal.
    /// Returns the amount actually consumed from this budget (may be less than requested
    /// if this budget doesn't have enough remaining — caller should distribute across budgets).
    /// </summary>
    public decimal DeductSpend(decimal amount)
    {
        if (Status != BudgetStatus.Active)
            throw new BudgetDomainException($"Cannot deduct spend from a budget in {Status} state.");
        if (amount <= 0)
            throw new BudgetDomainException("Spend amount must be positive.");

        var deductible = Math.Min(amount, RemainingTotal);
        RemainingTotal -= deductible;
        UpdatedAt = DateTime.UtcNow;

        if (RemainingTotal <= 0)
            Complete();

        return deductible;
    }

    public bool IsActiveOn(DateTime date) =>
        Status == BudgetStatus.Active &&
        date.Date >= StartDate &&
        date.Date <= EndDate;

    public bool HasEnded => DateTime.UtcNow.Date > EndDate;
}
