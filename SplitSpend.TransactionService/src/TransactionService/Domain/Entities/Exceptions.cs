namespace TransactionService.Domain.Entities;

public class TransactionDomainException : Exception
{
    public TransactionDomainException(string message) : base(message) { }
}

public class TransactionNotFoundException : Exception
{
    public TransactionNotFoundException(Guid id)
        : base($"Transaction {id} not found.") { }
}

public class TransactionNotOwnedException : Exception
{
    public TransactionNotOwnedException(Guid id, Guid userId)
        : base($"Transaction {id} does not belong to user {userId}.") { }
}

public class DuplicateIdempotencyKeyException : Exception
{
    public DuplicateIdempotencyKeyException(string key)
        : base($"Operation with idempotency key '{key}' already processed.") { }
}
