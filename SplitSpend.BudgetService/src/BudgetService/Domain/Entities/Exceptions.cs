namespace BudgetService.Domain.Entities;

public class BudgetDomainException : Exception
{
    public BudgetDomainException(string message) : base(message) { }
}

public class BudgetNotFoundException : Exception
{
    public BudgetNotFoundException(Guid budgetId)
        : base($"Budget {budgetId} not found.") { }
}

public class BudgetNotOwnedException : Exception
{
    public BudgetNotOwnedException(Guid budgetId, Guid userId)
        : base($"Budget {budgetId} does not belong to user {userId}.") { }
}

public class DuplicateIdempotencyKeyException : Exception
{
    public DuplicateIdempotencyKeyException(string key)
        : base($"Operation with idempotency key '{key}' already processed.") { }
}

public class InsufficientWalletBalanceException : Exception
{
    public InsufficientWalletBalanceException(decimal required, decimal available)
        : base($"Insufficient wallet balance. Required: ₦{required:N2}, Available: ₦{available:N2}.") { }
}
