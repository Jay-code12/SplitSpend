using PaymentService.Application.DTOs;
using PaymentService.Domain.Entities;

namespace PaymentService.Application.Interfaces;

public interface IPaymentLogRepository
{
    Task<PaymentLog?> GetByReferenceAsync(string reference, CancellationToken ct = default);
    Task<List<PaymentLog>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task AddAsync(PaymentLog log, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IVirtualAccountRepository
{
    Task<VirtualAccount?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<VirtualAccount?> GetByAccountNumberAsync(string accountNumber, CancellationToken ct = default);
    Task AddAsync(VirtualAccount account, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IIdempotencyRepository
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task MarkAsync(string key, CancellationToken ct = default);
}

public interface IKafkaPublisher
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default) where T : class;
}

public interface IPaystackClient
{
    /// <summary>
    /// Verifies HMAC-SHA512 signature on an incoming webhook.
    /// Must be the first thing called for every webhook request.
    /// </summary>
    bool VerifyWebhookSignature(string rawPayload, string signature);

    /// <summary>
    /// Re-verifies a charge directly against Paystack's API.
    /// Used by the manual verify endpoint for recovery after missed webhooks.
    /// </summary>
    Task<PaystackVerifyResponse> VerifyChargeAsync(
        string reference, CancellationToken ct = default);

    /// <summary>
    /// Creates a Paystack customer and dedicated virtual account for a new SplitSpend user.
    /// Called during user registration flow.
    /// </summary>
    Task<PaystackVirtualAccountResult> CreateVirtualAccountAsync(
        string email, string firstName, string lastName,
        string phone, CancellationToken ct = default);
}

// Paystack API response shapes used by the client
public record PaystackVerifyResponse(
    bool Success,
    string Status,          // "success" | "failed" | "abandoned"
    decimal Amount,         // Already in Naira (converted from kobo by client)
    string Reference,
    string? GatewayResponse,
    string? Channel
);

public record PaystackVirtualAccountResult(
    string AccountNumber,
    string AccountName,
    string BankName,
    string BankCode,
    string CustomerCode
);
