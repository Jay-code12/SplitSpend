namespace TransactionService.Domain.Enums;

public enum TransactionStatus
{
    Pending,     // Record opened; waiting for first processing step
    Processing,  // Money is in motion (debit confirmed, awaiting credit or external confirmation)
    Completed,   // Full lifecycle success confirmed
    Failed       // Any step in the chain failed
}

public enum TransactionType
{
    Deposit,           // Paystack virtual account → Main Balance
    InPlatformPayment, // Payer → Recipient (internal wallet move)
    ExternalTransfer   // Main Balance → External Nigerian bank (via Paystack Transfers API)
}

public enum DebitSource
{
    Budget,  // BudgetBalance was debited
    Main,    // MainBalance was debited
    None     // No debit (deposits have no debit source)
}
