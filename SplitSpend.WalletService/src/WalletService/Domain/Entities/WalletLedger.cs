namespace WalletService.Domain.Entities;

/// <summary>
/// Immutable audit record for every balance change in the wallet.
/// Captures before/after snapshots for both Main and Budget balances.
/// </summary>
public class WalletLedger
{
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public Guid UserId { get; private set; }

    public LedgerEntryType EntryType { get; private set; }  // Credit / Debit / InternalTransfer
    public DebitSource? DebitSource { get; private set; }   // Budget / Main (null for credits)

    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "NGN";

    // Before snapshots
    public decimal MainBalanceBefore { get; private set; }
    public decimal BudgetBalanceBefore { get; private set; }

    // After snapshots
    public decimal MainBalanceAfter { get; private set; }
    public decimal BudgetBalanceAfter { get; private set; }

    public Guid? CounterpartyId { get; private set; }   // Payer or recipient for in-platform payments
    public string? TransactionReference { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime CreatedAt { get; private set; }

    // Navigation
    public Wallet Wallet { get; private set; } = null!;

    private WalletLedger() { }

    public static WalletLedger CreateCredit(
        Guid walletId, Guid userId,
        decimal amount,
        decimal mainBefore, decimal budgetBefore,
        decimal mainAfter, decimal budgetAfter,
        string idempotencyKey,
        Guid? counterpartyId = null,
        string? reference = null,
        string? description = null)
    {
        return new WalletLedger
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            UserId = userId,
            EntryType = LedgerEntryType.Credit,
            DebitSource = null,
            Amount = amount,
            MainBalanceBefore = mainBefore,
            BudgetBalanceBefore = budgetBefore,
            MainBalanceAfter = mainAfter,
            BudgetBalanceAfter = budgetAfter,
            CounterpartyId = counterpartyId,
            TransactionReference = reference,
            IdempotencyKey = idempotencyKey,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static WalletLedger CreateDebit(
        Guid walletId, Guid userId,
        decimal amount,
        DebitSource debitSource,
        decimal mainBefore, decimal budgetBefore,
        decimal mainAfter, decimal budgetAfter,
        string idempotencyKey,
        Guid? counterpartyId = null,
        string? reference = null,
        string? description = null)
    {
        return new WalletLedger
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            UserId = userId,
            EntryType = LedgerEntryType.Debit,
            DebitSource = debitSource,
            Amount = amount,
            MainBalanceBefore = mainBefore,
            BudgetBalanceBefore = budgetBefore,
            MainBalanceAfter = mainAfter,
            BudgetBalanceAfter = budgetAfter,
            CounterpartyId = counterpartyId,
            TransactionReference = reference,
            IdempotencyKey = idempotencyKey,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static WalletLedger CreateInternalTransfer(
        Guid walletId, Guid userId,
        decimal amount,
        LedgerEntryType direction, // InternalTransferOut (Main→Budget) or InternalTransferIn (Budget→Main)
        decimal mainBefore, decimal budgetBefore,
        decimal mainAfter, decimal budgetAfter,
        string idempotencyKey,
        string? description = null)
    {
        return new WalletLedger
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            UserId = userId,
            EntryType = direction,
            DebitSource = null,
            Amount = amount,
            MainBalanceBefore = mainBefore,
            BudgetBalanceBefore = budgetBefore,
            MainBalanceAfter = mainAfter,
            BudgetBalanceAfter = budgetAfter,
            IdempotencyKey = idempotencyKey,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };
    }
}
