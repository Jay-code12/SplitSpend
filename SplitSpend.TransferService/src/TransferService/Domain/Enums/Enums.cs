namespace TransferService.Domain.Enums;

public enum TransferStatus
{
    Pending,     // Record created; wallet pre-debit requested
    Processing,  // Wallet confirmed pre-debit; Paystack payout initiated
    Completed,   // Paystack confirmed bank delivery
    Failed,      // Paystack declined or timed out; wallet reversal triggered
    Reversed     // Wallet reversal confirmed after failure
}
