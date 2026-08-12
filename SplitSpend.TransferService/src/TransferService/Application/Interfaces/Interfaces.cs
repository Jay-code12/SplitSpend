using TransferService.Application.DTOs;
using TransferService.Domain.Entities;

namespace TransferService.Application.Interfaces;

public interface ITransferRepository
{
    Task<BankTransfer?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<BankTransfer> GetByIdRequiredAsync(Guid id, CancellationToken ct = default);
    Task<BankTransfer?> GetByPaystackReferenceAsync(string reference, CancellationToken ct = default);
    Task<List<BankTransfer>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all Processing transfers older than 24 hours for auto-reversal.
    /// </summary>
    Task<List<BankTransfer>> GetTimedOutTransfersAsync(CancellationToken ct = default);

    Task AddAsync(BankTransfer transfer, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IBeneficiaryRepository
{
    Task<BankBeneficiary?> GetAsync(Guid userId, string accountNumber, string bankCode, CancellationToken ct = default);
    Task<List<BankBeneficiary>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task AddAsync(BankBeneficiary beneficiary, CancellationToken ct = default);
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
    /// Initiates a payout to an external Nigerian bank account via Paystack Transfers API.
    /// Returns the Paystack transfer code on success.
    /// </summary>
    Task<string> InitiateTransferAsync(
        string accountNumber,
        string bankCode,
        string accountName,
        decimal amount,
        string reference,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies an account number with a Nigerian bank via Paystack's account resolution API.
    /// Returns the resolved account name.
    /// </summary>
    Task<VerifyAccountResponse> VerifyAccountAsync(
        string accountNumber,
        string bankCode,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the list of supported Nigerian banks from Paystack.
    /// </summary>
    Task<List<NigerianBank>> GetBanksAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifies the HMAC-SHA512 signature on an incoming Paystack webhook.
    /// Must be called before processing any webhook payload.
    /// </summary>
    bool VerifyWebhookSignature(string payload, string signature);

    /// <summary>
    /// Queries Paystack for the current status of a transfer.
    /// Used for polling during recovery / timeout checks.
    /// </summary>
    Task<string> GetTransferStatusAsync(string transferCode, CancellationToken ct = default);
}

public interface IWalletServiceClient
{
    /// <summary>
    /// Verifies the user has sufficient Main Balance for the transfer amount.
    /// Sync REST call — called as a pre-flight before creating the transfer record.
    /// </summary>
    Task<decimal> GetMainBalanceAsync(Guid userId, CancellationToken ct = default);
}
