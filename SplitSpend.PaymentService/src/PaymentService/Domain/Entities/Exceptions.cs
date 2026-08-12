namespace PaymentService.Domain.Entities;

public class PaymentDomainException : Exception
{
    public PaymentDomainException(string message) : base(message) { }
}

public class InvalidWebhookSignatureException : Exception
{
    public InvalidWebhookSignatureException()
        : base("Paystack webhook HMAC-SHA512 signature is invalid. Request rejected.") { }
}

public class DuplicatePaymentException : Exception
{
    public DuplicatePaymentException(string reference)
        : base($"Payment with reference '{reference}' has already been processed.") { }
}

public class VirtualAccountNotFoundException : Exception
{
    public VirtualAccountNotFoundException(Guid userId)
        : base($"No virtual account found for user {userId}.") { }
}

public class UserResolutionException : Exception
{
    public UserResolutionException(string accountNumber)
        : base($"Could not resolve a SplitSpend user for virtual account {accountNumber}.") { }
}
