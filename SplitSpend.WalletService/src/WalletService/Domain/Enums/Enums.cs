namespace WalletService.Domain.Enums;

public enum WalletStatus
{
    Active,
    Suspended,
    Closed
}

public enum LedgerEntryType
{
    Credit,
    Debit,
    InternalTransferOut,   // Main → Budget
    InternalTransferIn     // Budget → Main
}

public enum DebitSource
{
    Budget,
    Main
}

public enum BalanceType
{
    Main,
    Budget
}
