namespace BudgetService.Domain.Enums;

public enum BudgetStatus
{
    Pending,    // Created; waiting for Wallet to confirm fund transfer
    Active,     // Wallet confirmed transfer; spending can begin
    Completed,  // All funds consumed OR end date reached
    Cancelled,  // User cancelled; remaining funds returned to Main
    Failed      // Wallet transfer failed; budget never activated
}

public enum BudgetSource
{
    Self,   // User funded their own budget
    Gift    // Received as a gift from another user
}

public enum GiftStatus
{
    Pending,   // Gift sent; waiting for wallet move
    Completed, // Wallet credited the receiver
    Failed     // Wallet move failed
}
