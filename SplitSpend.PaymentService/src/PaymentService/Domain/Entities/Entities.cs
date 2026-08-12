using PaymentService.Domain.Enums;

namespace PaymentService.Domain.Entities;

/// <summary>
/// Immutable audit record for every Paystack deposit webhook received.
/// One PaymentLog per charge.success event — created once and never mutated.
///
/// Payment Service has one job: verify Paystack deposits and emit payment.successful.
/// All wallet crediting happens in Wallet Service after it consumes that event.
/// This log exists purely for audit, reconciliation, and the manual verify endpoint.
/// </summary>
public class PaymentLog
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "NGN";

    public PaymentStatus Status { get; private set; }
    public PaymentType Type { get; private set; }

    // Paystack identifiers
    public string PaystackReference { get; private set; } = string.Empty;
    public string PaystackTransactionId { get; private set; } = string.Empty;
    public string? Channel { get; private set; }        // "bank_transfer", "card" etc.
    public string? GatewayResponse { get; private set; } // Paystack's status message

    // Raw webhook stored for audit + replay
    public string RawWebhookPayload { get; private set; } = string.Empty;

    // Idempotency
    public string IdempotencyKey { get; private set; } = string.Empty;

    public DateTime PaidAt { get; private set; }     // When Paystack says payment occurred
    public DateTime CreatedAt { get; private set; }  // When we processed the webhook

    private PaymentLog() { }

    public static PaymentLog CreateSuccess(
        Guid userId,
        decimal amount,
        string paystackReference,
        string paystackTransactionId,
        string idempotencyKey,
        string rawWebhookPayload,
        string? channel = null,
        string? gatewayResponse = null,
        DateTime? paidAt = null)
    {
        if (amount <= 0)
            throw new PaymentDomainException("Payment amount must be positive.");
        if (string.IsNullOrWhiteSpace(paystackReference))
            throw new PaymentDomainException("Paystack reference is required.");

        return new PaymentLog
        {
            Id                    = Guid.NewGuid(),
            UserId                = userId,
            Amount                = amount,
            Currency              = "NGN",
            Status                = PaymentStatus.Success,
            Type                  = PaymentType.Deposit,
            PaystackReference     = paystackReference,
            PaystackTransactionId = paystackTransactionId,
            IdempotencyKey        = idempotencyKey,
            RawWebhookPayload     = rawWebhookPayload,
            Channel               = channel,
            GatewayResponse       = gatewayResponse,
            PaidAt                = paidAt ?? DateTime.UtcNow,
            CreatedAt             = DateTime.UtcNow
        };
    }

    public static PaymentLog CreateFailed(
        Guid userId,
        decimal amount,
        string? paystackReference,
        string idempotencyKey,
        string rawWebhookPayload)
    {
        return new PaymentLog
        {
            Id                    = Guid.NewGuid(),
            UserId                = userId,
            Amount                = amount,
            Currency              = "NGN",
            Status                = PaymentStatus.Failed,
            Type                  = PaymentType.Deposit,
            PaystackReference     = paystackReference ?? string.Empty,
            PaystackTransactionId = string.Empty,
            IdempotencyKey        = idempotencyKey,
            RawWebhookPayload     = rawWebhookPayload,
            PaidAt                = DateTime.UtcNow,
            CreatedAt             = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Paystack virtual account assigned to each user at registration.
/// When a user bank-transfers money here, Paystack fires charge.success to our webhook.
/// One VirtualAccount per user — created via Paystack Dedicated Virtual Account API.
/// </summary>
public class VirtualAccount
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string AccountNumber { get; private set; } = string.Empty;
    public string AccountName { get; private set; } = string.Empty;
    public string BankName { get; private set; } = string.Empty;
    public string BankCode { get; private set; } = string.Empty;
    public string PaystackCustomerCode { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private VirtualAccount() { }

    public static VirtualAccount Create(
        Guid userId,
        string accountNumber,
        string accountName,
        string bankName,
        string bankCode,
        string paystackCustomerCode)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new PaymentDomainException("Account number is required.");
        if (string.IsNullOrWhiteSpace(paystackCustomerCode))
            throw new PaymentDomainException("Paystack customer code is required.");

        return new VirtualAccount
        {
            Id                    = Guid.NewGuid(),
            UserId                = userId,
            AccountNumber         = accountNumber,
            AccountName           = accountName,
            BankName              = bankName,
            BankCode              = bankCode,
            PaystackCustomerCode  = paystackCustomerCode,
            IsActive              = true,
            CreatedAt             = DateTime.UtcNow
        };
    }

    public void Deactivate() => IsActive = false;
}
