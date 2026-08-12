using System.Net.Http.Headers;
using System.Text.Json;
using SplitSpend.Gateway.Models;
using SplitSpend.Gateway.Services;

namespace SplitSpend.Gateway.Aggregators;

public interface IDashboardAggregator
{
    Task<DashboardResponse> AggregateAsync(
        string userId, string? bearerToken, string correlationId,
        CancellationToken ct = default);
}

/// <summary>
/// Fans out three parallel HTTP calls to Wallet Service, Budget Service, and
/// Transaction Service, then merges the results into a single DashboardResponse.
/// Each downstream call carries the CorrelationId and Bearer token so tracing
/// and authentication are preserved end-to-end.
/// </summary>
public sealed class DashboardAggregator(
    IHttpClientFactory   httpFactory,
    IConsulService       consul,
    ILogger<DashboardAggregator> logger) : IDashboardAggregator
{
    public async Task<DashboardResponse> AggregateAsync(
        string userId, string? bearerToken, string correlationId,
        CancellationToken ct = default)
    {
        // ── Resolve all three service addresses from Consul in parallel ────
        var (walletEntry, budgetEntry, txEntry) = await (
            consul.ResolveAsync("wallet-service",      ct),
            consul.ResolveAsync("budget-service",      ct),
            consul.ResolveAsync("transaction-service", ct)
        );

        if (walletEntry is null || budgetEntry is null || txEntry is null)
        {
            logger.LogError(
                "Dashboard aggregation failed — one or more services unavailable. " +
                "Wallet={WalletOk} Budget={BudgetOk} Transaction={TxOk}",
                walletEntry is not null, budgetEntry is not null, txEntry is not null);

            throw new InvalidOperationException(
                "One or more required services are unavailable. Please try again shortly.");
        }

        // ── Fan out parallel requests ──────────────────────────────────────
        var client = httpFactory.CreateClient("aggregator");

        using var walletReq = BuildRequest(
            $"{walletEntry.BaseUrl}/api/wallets/{userId}",
            bearerToken, correlationId);

        using var budgetReq = BuildRequest(
            $"{budgetEntry.BaseUrl}/api/budgets/{userId}/daily",
            bearerToken, correlationId);

        using var txReq = BuildRequest(
            $"{txEntry.BaseUrl}/api/transactions/{userId}?page=1&pageSize=5",
            bearerToken, correlationId);

        logger.LogInformation(
            "Dashboard aggregation started. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);

        var (walletResp, budgetResp, txResp) = await (
            client.SendAsync(walletReq, ct),
            client.SendAsync(budgetReq, ct),
            client.SendAsync(txReq,     ct)
        );

        // ── Parse responses — partial failures degrade gracefully ──────────
        var wallet = await ParseOrDefaultAsync<WalletSummary>(walletResp,  "wallet",      logger);
        var budget = await ParseOrDefaultAsync<BudgetSummary>(budgetResp,  "budget",      logger);
        var txList = await ParseOrDefaultAsync<List<object>> (txResp,      "transaction", logger);

        logger.LogInformation(
            "Dashboard aggregation completed. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);

        return new DashboardResponse
        {
            Wallet       = wallet ?? new WalletSummary(),
            Budget       = budget ?? new BudgetSummary(),
            Transactions = txList ?? [],
            TraceId      = correlationId
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildRequest(
        string url, string? bearerToken, string correlationId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add(GatewayHeaders.CorrelationId, correlationId);

        if (!string.IsNullOrWhiteSpace(bearerToken))
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);

        return req;
    }

    private static async Task<T?> ParseOrDefaultAsync<T>(
        HttpResponseMessage response,
        string serviceName,
        ILogger logger) where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Aggregation partial failure. Service={Service} Status={Status}",
                serviceName, (int)response.StatusCode);
            return null;
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "Failed to deserialize aggregation response. Service={Service}", serviceName);
            return null;
        }
    }
}
