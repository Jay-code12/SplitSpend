using System.Net.Http.Json;
using BudgetService.Application.Interfaces;
using BudgetService.Domain.Entities;

namespace BudgetService.Infrastructure;

/// <summary>
/// Typed HTTP client for synchronous REST calls to Wallet Service.
/// Used only for pre-flight balance checks before budget/gift creation.
/// Circuit breaker via Polly (configured in Program.cs).
/// Consul is used for service discovery — the base URL is resolved at startup.
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
                throw new InvalidOperationException($"Wallet Service returned null for user {userId}.");

            _log.LogDebug(
                "Wallet balance check for {UserId}: Main=₦{Main}",
                userId, response.MainBalance);

            return response.MainBalance;
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "Failed to reach Wallet Service for user {UserId}", userId);
            throw new InvalidOperationException("Could not verify wallet balance — Wallet Service unavailable.", ex);
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
