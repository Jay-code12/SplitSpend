namespace WalletService.Domain.Entities;

public class InsufficientFundsException : Exception
{
    public InsufficientFundsException(string message) : base(message) { }
}

public class WalletNotFoundException : Exception
{
    public WalletNotFoundException(Guid userId)
        : base($"Wallet not found for user {userId}.") { }
}

public class DuplicateIdempotencyKeyException : Exception
{
    public DuplicateIdempotencyKeyException(string key)
        : base($"Operation with idempotency key '{key}' has already been processed.") { }
}

public class WalletSuspendedException : Exception
{
    public WalletSuspendedException(Guid userId)
        : base($"Wallet for user {userId} is suspended.") { }
}
