using PaymentService.Domain.Entities;

namespace PaymentService.Application.Interfaces;

/// <summary>
/// Extension to IVirtualAccountRepository for the customer-code lookup
/// used during webhook user resolution.
/// </summary>
public partial interface IVirtualAccountRepository
{
    /// <summary>
    /// Looks up a VirtualAccount by Paystack customer code.
    /// Used in HandleDepositWebhookAsync to resolve which SplitSpend user
    /// a deposit belongs to, given only the Paystack customer identifier.
    /// </summary>
    Task<VirtualAccount?> GetByCustomerCodeAsync(
        string customerCode, CancellationToken ct = default);
}
