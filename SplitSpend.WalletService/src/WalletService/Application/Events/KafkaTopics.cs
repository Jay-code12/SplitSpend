namespace WalletService.Application.Events;

public static class KafkaTopics
{
    // Produced by Wallet Service
    public const string WalletCredited               = "wallet.credited";
    public const string WalletBudgetDebited          = "wallet.budget.debited";
    public const string WalletMainDebited            = "wallet.main.debited";
    public const string WalletBudgetTransferComplete = "wallet.budget.transfer.completed";
    public const string WalletBudgetTransferFailed   = "wallet.budget.transfer.failed";
    public const string WalletMainTransferInitiated  = "wallet.main.transfer.initiated";
    public const string WalletInsufficientFunds      = "wallet.insufficient_funds";

    // Consumed by Wallet Service
    public const string VendorPaymentApproved        = "vendor.payment.approved";
    public const string PaymentSuccessful            = "payment.successful";
    public const string BudgetCreated               = "budget.created";
    public const string BudgetDailyExpired          = "budget.daily.expired";
    public const string GiftSent                    = "gift.sent";
    public const string BudgetCancelled             = "budget.cancelled";
    public const string TransferFailed              = "transfer.failed";
}
