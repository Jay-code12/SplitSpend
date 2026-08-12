using System.Net.Http.Json;
using TransferService.Application.Interfaces;

namespace TransferService.Infrastructure.Http;

/// <summary>
/// Sync REST call to Wallet Service for pre-flight balance check before initiating a transfer.
/// Polly retry + circuit breaker configured in Program.cs.
/// </summary>
public class WalletServiceClient : IWalletServiceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<WalletServiceClient> _log;

    public WalletServiceClient(HttpClient http, ILogger<WalletServiceClient> log)
    {
        _http = http;
        _log  = log;
    }

    public async Task<decimal> GetMainBalanceAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetFromJsonAsync<WalletBalanceDto>(
                $"api/wallets/{userId}", ct);

            if (response == null)
                throw new InvalidOperationException(
                    $"Wallet Service returned null balance for user {userId}.");

            _log.LogDebug("Wallet balance for {UserId}: Main=₦{Main}", userId, response.MainBalance);

            return response.MainBalance;
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "Could not reach Wallet Service for user {UserId}", userId);
            throw new InvalidOperationException(
                "Could not verify wallet balance — Wallet Service unavailable.", ex);
        }
    }

    private record WalletBalanceDto(
        Guid WalletId,
        Guid UserId,
        decimal MainBalance,
        decimal BudgetBalance,
        string Currency,
        string Status);
}
