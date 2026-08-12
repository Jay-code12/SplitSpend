namespace WalletService.Domain.Entities;

public class Wallet
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public decimal MainBalance { get; private set; }
    public decimal BudgetBalance { get; private set; }
    public string Currency { get; private set; } = "NGN";
    public WalletStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // EF Core constructor
    private Wallet() { }

    public static Wallet Create(Guid userId)
    {
        return new Wallet
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MainBalance = 0,
            BudgetBalance = 0,
            Currency = "NGN",
            Status = WalletStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public void CreditMain(decimal amount)
    {
        if (amount <= 0) throw new ArgumentException("Credit amount must be positive.", nameof(amount));
        MainBalance += amount;
        UpdatedAt = DateTime.UtcNow;
    }

    public void CreditBudget(decimal amount)
    {
        if (amount <= 0) throw new ArgumentException("Credit amount must be positive.", nameof(amount));
        BudgetBalance += amount;
        UpdatedAt = DateTime.UtcNow;
    }

    public void DebitMain(decimal amount)
    {
        if (amount <= 0) throw new ArgumentException("Debit amount must be positive.", nameof(amount));
        if (MainBalance < amount) throw new InsufficientFundsException("Insufficient main balance.");
        MainBalance -= amount;
        UpdatedAt = DateTime.UtcNow;
    }

    public void DebitBudget(decimal amount)
    {
        if (amount <= 0) throw new ArgumentException("Debit amount must be positive.", nameof(amount));
        if (BudgetBalance < amount) throw new InsufficientFundsException("Insufficient budget balance.");
        BudgetBalance -= amount;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Budget-first debit logic.
    /// Returns (budgetDebited, mainDebited) showing how much came from each balance.
    /// Throws InsufficientFundsException if neither covers the full amount.
    /// </summary>
    public (decimal BudgetDebited, decimal MainDebited) DebitBudgetFirst(decimal amount)
    {
        if (amount <= 0) throw new ArgumentException("Debit amount must be positive.", nameof(amount));

        decimal budgetDebited = 0;
        decimal mainDebited = 0;
        decimal remaining = amount;

        if (BudgetBalance >= remaining)
        {
            // Budget covers the full amount
            BudgetBalance -= remaining;
            budgetDebited = remaining;
            remaining = 0;
        }
        else if (BudgetBalance > 0)
        {
            // Budget partially covers — use it all, then fall back to Main
            budgetDebited = BudgetBalance;
            remaining -= BudgetBalance;
            BudgetBalance = 0;
        }

        if (remaining > 0)
        {
            if (MainBalance < remaining)
                throw new InsufficientFundsException("Insufficient funds in both budget and main balance.");

            MainBalance -= remaining;
            mainDebited = remaining;
        }

        UpdatedAt = DateTime.UtcNow;
        return (budgetDebited, mainDebited);
    }

    /// <summary>
    /// Move funds from Main to Budget (budget creation / gift funding).
    /// </summary>
    public void TransferMainToBudget(decimal amount)
    {
        if (amount <= 0) throw new ArgumentException("Transfer amount must be positive.", nameof(amount));
        if (MainBalance < amount) throw new InsufficientFundsException("Insufficient main balance for budget transfer.");
        MainBalance -= amount;
        BudgetBalance += amount;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Move funds from Budget back to Main (daily expiry / budget cancellation).
    /// </summary>
    public void TransferBudgetToMain(decimal amount)
    {
        if (amount <= 0) throw new ArgumentException("Transfer amount must be positive.", nameof(amount));
        if (BudgetBalance < amount) throw new InsufficientFundsException("Insufficient budget balance for return.");
        BudgetBalance -= amount;
        MainBalance += amount;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Suspend() { Status = WalletStatus.Suspended; UpdatedAt = DateTime.UtcNow; }
    public void Activate() { Status = WalletStatus.Active; UpdatedAt = DateTime.UtcNow; }
}
