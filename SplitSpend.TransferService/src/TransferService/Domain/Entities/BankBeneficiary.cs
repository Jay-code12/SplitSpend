namespace TransferService.Domain.Entities;

/// <summary>
/// Cached beneficiary record so users don't re-enter account details for repeat transfers.
/// Populated after the first successful Paystack account lookup for a given account number + bank.
/// </summary>
public class BankBeneficiary
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string AccountNumber { get; private set; } = string.Empty;
    public string BankCode { get; private set; } = string.Empty;
    public string BankName { get; private set; } = string.Empty;
    public string AccountName { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private BankBeneficiary() { }

    public static BankBeneficiary Create(
        Guid userId,
        string accountNumber,
        string bankCode,
        string bankName,
        string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new TransferDomainException("Account number is required.");
        if (string.IsNullOrWhiteSpace(bankCode))
            throw new TransferDomainException("Bank code is required.");
        if (string.IsNullOrWhiteSpace(accountName))
            throw new TransferDomainException("Account name is required.");

        return new BankBeneficiary
        {
            Id            = Guid.NewGuid(),
            UserId        = userId,
            AccountNumber = accountNumber,
            BankCode      = bankCode,
            BankName      = bankName,
            AccountName   = accountName,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow
        };
    }

    public void UpdateAccountName(string accountName)
    {
        AccountName = accountName;
        UpdatedAt   = DateTime.UtcNow;
    }
}

// ── Domain exceptions ────────────────────────────────────────────────────────

public class TransferDomainException : Exception
{
    public TransferDomainException(string message) : base(message) { }
}

public class TransferNotFoundException : Exception
{
    public TransferNotFoundException(Guid transferId)
        : base($"Transfer {transferId} not found.") { }
}

public class TransferNotOwnedException : Exception
{
    public TransferNotOwnedException(Guid transferId, Guid userId)
        : base($"Transfer {transferId} does not belong to user {userId}.") { }
}

public class DuplicateIdempotencyKeyException : Exception
{
    public DuplicateIdempotencyKeyException(string key)
        : base($"Operation with idempotency key '{key}' already processed.") { }
}

public class InvalidWebhookSignatureException : Exception
{
    public InvalidWebhookSignatureException()
        : base("Paystack webhook signature is invalid. Request rejected.") { }
}

public class PaystackApiException : Exception
{
    public int StatusCode { get; }
    public PaystackApiException(string message, int statusCode = 0)
        : base(message) { StatusCode = statusCode; }
}
