using BudgetService.Domain.Enums;

namespace BudgetService.Domain.Entities;

/// <summary>
/// Tracks a gift budget sent from one user to another.
/// When accepted: Wallet debits sender Main, credits receiver Budget.
/// Gift budgets cannot be cancelled by the receiver.
/// </summary>
public class GiftBudget
{
    public Guid Id { get; private set; }
    public Guid SenderUserId { get; private set; }
    public Guid ReceiverUserId { get; private set; }
    public decimal Amount { get; private set; }
    public int DurationDays { get; private set; }        // How many days the resulting budget runs
    public GiftStatus Status { get; private set; }
    public Guid? ResultingBudgetId { get; private set; } // The Budget created for the receiver
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string? Message { get; private set; }         // Optional personal message
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private GiftBudget() { }

    public static GiftBudget Create(
        Guid senderUserId,
        Guid receiverUserId,
        decimal amount,
        int durationDays,
        string idempotencyKey,
        string? message = null)
    {
        if (amount <= 0)
            throw new BudgetDomainException("Gift amount must be positive.");
        if (durationDays <= 0)
            throw new BudgetDomainException("Gift duration must be at least 1 day.");
        if (senderUserId == receiverUserId)
            throw new BudgetDomainException("Cannot send a gift budget to yourself.");

        return new GiftBudget
        {
            Id              = Guid.NewGuid(),
            SenderUserId    = senderUserId,
            ReceiverUserId  = receiverUserId,
            Amount          = amount,
            DurationDays    = durationDays,
            Status          = GiftStatus.Pending,
            IdempotencyKey  = idempotencyKey,
            Message         = message,
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow
        };
    }

    public void MarkCompleted(Guid resultingBudgetId)
    {
        Status             = GiftStatus.Completed;
        ResultingBudgetId  = resultingBudgetId;
        UpdatedAt          = DateTime.UtcNow;
    }

    public void MarkFailed()
    {
        Status    = GiftStatus.Failed;
        UpdatedAt = DateTime.UtcNow;
    }
}
