namespace PaymentService.Application.DTOs;

// ── Paystack webhook shapes ───────────────────────────────────────────────────

/// <summary>
/// Top-level Paystack webhook envelope.
/// Payment Service only handles event = "charge.success".
/// </summary>
public record PaystackWebhookRequest(
    string Event,
    PaystackChargeData Data
);

public record PaystackChargeData(
    string Reference,
    string Status,           // "success"
    long Amount,             // Kobo — divide by 100 for Naira
    string Currency,
    string? Channel,
    string? GatewayResponse,
    string? PaidAt,
    PaystackCustomer Customer,
    PaystackAuthorization? Authorization
);

public record PaystackCustomer(
    long Id,
    string Email,
    string CustomerCode
);

public record PaystackAuthorization(
    string AuthorizationCode,
    string CardType,
    string Last4,
    string Bank
);

// ── Virtual Account provisioning ──────────────────────────────────────────────

/// <summary>Request from user-service or auth flow to provision a new virtual account.</summary>
public record ProvisionVirtualAccountRequest(
    Guid UserId,
    string Email,
    string FirstName,
    string LastName,
    string Phone
);

public record VirtualAccountResponse(
    Guid Id,
    Guid UserId,
    string AccountNumber,
    string AccountName,
    string BankName,
    string BankCode,
    bool IsActive,
    DateTime CreatedAt
);

// ── Payment Log responses ─────────────────────────────────────────────────────

public record PaymentLogResponse(
    Guid Id,
    Guid UserId,
    decimal Amount,
    string Currency,
    string Status,
    string PaystackReference,
    string? Channel,
    string? GatewayResponse,
    DateTime PaidAt,
    DateTime CreatedAt
);

public record WebhookAcknowledgement(
    bool Processed,
    string Message
);

public record ManualVerifyResponse(
    string Reference,
    string Status,
    decimal Amount,
    bool AlreadyProcessed
);
