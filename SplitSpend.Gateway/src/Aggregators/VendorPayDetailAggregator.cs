using System.Net.Http.Headers;
using System.Text.Json;
using SplitSpend.Gateway.Models;
using SplitSpend.Gateway.Services;

namespace SplitSpend.Gateway.Aggregators;

public interface IVendorPayDetailAggregator
{
    Task<VendorPayDetailResponse> AggregateAsync(
        string requestId, string? buyerUserId, string? bearerToken,
        string correlationId, CancellationToken ct = default);
}

/// <summary>
/// Fans out three parallel calls to Vendor Pay Service, User Service (vendor profile),
/// and Wallet Service (buyer's balance) — all the data a buyer needs to make an
/// informed approval decision on a single screen.
/// </summary>
public sealed class VendorPayDetailAggregator(
    IHttpClientFactory           httpFactory,
    IConsulService               consul,
    ILogger<VendorPayDetailAggregator> logger) : IVendorPayDetailAggregator
{
    public async Task<VendorPayDetailResponse> AggregateAsync(
        string requestId, string? buyerUserId, string? bearerToken,
        string correlationId, CancellationToken ct = default)
    {
        var (vendorPayEntry, userEntry, walletEntry) = await (
            consul.ResolveAsync("vendor-pay-service", ct),
            consul.ResolveAsync("user-service",       ct),
            consul.ResolveAsync("wallet-service",     ct)
        );

        if (vendorPayEntry is null || userEntry is null || walletEntry is null)
        {
            logger.LogError(
                "VendorPayDetail aggregation failed — service unavailable. CorrelationId={CorrelationId}",
                correlationId);
            throw new InvalidOperationException("One or more required services are unavailable.");
        }

        var client = httpFactory.CreateClient("aggregator");

        using var payReq = BuildRequest(
            $"{vendorPayEntry.BaseUrl}/api/vendor-pay/{requestId}",
            bearerToken, correlationId);

        // We need to know the vendorId from the payment request before fetching the profile.
        // We fetch the payment request first, then fan out for vendor profile and buyer balance.
        var payResp = await client.SendAsync(payReq, ct);

        string? vendorId = null;
        object? paymentRequest = null;

        if (payResp.IsSuccessStatusCode)
        {
            var json = await payResp.Content.ReadAsStringAsync();
            var doc  = JsonDocument.Parse(json);
            paymentRequest = doc;
            vendorId = doc.RootElement.TryGetProperty("requesterId", out var v) ? v.GetString() : null;
        }

        // Now fan out vendor profile + buyer balance in parallel
        using var vendorReq = BuildRequest(
            $"{userEntry.BaseUrl}/api/users/{vendorId ?? "unknown"}",
            bearerToken, correlationId);

        using var balanceReq = BuildRequest(
            $"{walletEntry.BaseUrl}/api/wallets/{buyerUserId}",
            bearerToken, correlationId);

        var (vendorResp, balanceResp) = await (
            client.SendAsync(vendorReq,  ct),
            client.SendAsync(balanceReq, ct)
        );

        var vendorProfile = await ParseOrDefaultAsync(vendorResp,  logger);
        var buyerBalance  = await ParseOrDefaultAsync(balanceResp, logger);

        return new VendorPayDetailResponse
        {
            PaymentRequest = paymentRequest,
            VendorProfile  = vendorProfile,
            BuyerBalance   = buyerBalance,
            TraceId        = correlationId
        };
    }

    private static HttpRequestMessage BuildRequest(
        string url, string? bearerToken, string correlationId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add(GatewayHeaders.CorrelationId, correlationId);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return req;
    }

    private static async Task<object?> ParseOrDefaultAsync(
        HttpResponseMessage response, ILogger logger)
    {
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Aggregation sub-call failed with status {Status}",
                (int)response.StatusCode);
            return null;
        }
        try
        {
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize aggregation sub-response.");
            return null;
        }
    }
}
