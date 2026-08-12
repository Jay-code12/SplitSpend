using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaymentService.Application.Interfaces;

namespace PaymentService.Infrastructure.Http;

/// <summary>
/// Typed HTTP client for all Paystack API calls made by Payment Service.
///
/// Endpoints used:
///   GET  /transaction/verify/{reference}   — manual re-verify a charge
///   POST /customer                         — create a Paystack customer
///   POST /dedicated_account                — assign a dedicated virtual account
///
/// Amount convention: Paystack sends/receives kobo. We convert to/from Naira here
/// so the rest of the codebase always works in Naira (decimal).
///
/// HMAC verification: X-Paystack-Signature = HMAC-SHA512(rawBody, secretKey) as hex.
/// </summary>
public class PaystackClient : IPaystackClient
{
    private readonly HttpClient _http;
    private readonly string _secretKey;
    private readonly ILogger<PaystackClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public PaystackClient(HttpClient http, IConfiguration config, ILogger<PaystackClient> log)
    {
        _http      = http;
        _secretKey = config["Paystack:SecretKey"]
                     ?? throw new InvalidOperationException("Paystack:SecretKey not configured.");
        _log       = log;
    }

    // ── HMAC webhook verification ─────────────────────────────────────────────

    /// <summary>
    /// Verifies the X-Paystack-Signature header.
    /// Signature = lowercase hex of HMAC-SHA512(rawBody, secretKey).
    /// This is called BEFORE any payload deserialization in the controller.
    /// </summary>
    public bool VerifyWebhookSignature(string rawPayload, string signature)
    {
        if (string.IsNullOrWhiteSpace(rawPayload) || string.IsNullOrWhiteSpace(signature))
            return false;

        using var hmac     = new HMACSHA512(Encoding.UTF8.GetBytes(_secretKey));
        var hash           = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawPayload));
        var computed       = Convert.ToHexString(hash).ToLowerInvariant();
        var isValid        = computed == signature.ToLowerInvariant();

        if (!isValid)
            _log.LogWarning(
                "Webhook signature mismatch. Computed={Computed} Received={Received}",
                computed, signature.ToLowerInvariant());

        return isValid;
    }

    // ── Manual charge re-verification ────────────────────────────────────────

    public async Task<PaystackVerifyResponse> VerifyChargeAsync(
        string reference, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"transaction/verify/{reference}", ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "Paystack verify failed for ref {Ref}: {Status} {Body}",
                reference, response.StatusCode, body);
            throw new InvalidOperationException(
                $"Paystack verify returned {(int)response.StatusCode}: {body}");
        }

        var result = JsonSerializer.Deserialize<PaystackApiResponse<PaystackTransactionData>>(
            body, JsonOpts);

        if (result?.Data == null)
            throw new InvalidOperationException("Paystack returned empty transaction data.");

        return new PaystackVerifyResponse(
            Success:         result.Status,
            Status:          result.Data.Status,
            Amount:          result.Data.Amount / 100m,   // kobo → Naira
            Reference:       result.Data.Reference,
            GatewayResponse: result.Data.GatewayResponse,
            Channel:         result.Data.Channel);
    }

    // ── Virtual account provisioning ──────────────────────────────────────────

    public async Task<PaystackVirtualAccountResult> CreateVirtualAccountAsync(
        string email, string firstName, string lastName,
        string phone, CancellationToken ct = default)
    {
        // Step 1: Create or retrieve a Paystack customer
        var customerCode = await EnsureCustomerAsync(email, firstName, lastName, phone, ct);

        // Step 2: Assign a dedicated virtual account (WEMA bank by default in Nigeria)
        var accountPayload = new
        {
            customer          = customerCode,
            preferred_bank    = "wema-bank",  // Most reliable DVA provider in Nigeria
            country           = "NG"
        };

        var accountResponse = await _http.PostAsJsonAsync("dedicated_account", accountPayload, ct);
        var accountBody     = await accountResponse.Content.ReadAsStringAsync(ct);

        if (!accountResponse.IsSuccessStatusCode)
        {
            _log.LogError(
                "Paystack dedicated account creation failed: {Status} {Body}",
                accountResponse.StatusCode, accountBody);
            throw new InvalidOperationException(
                $"Could not create virtual account: {accountBody}");
        }

        var accountResult = JsonSerializer.Deserialize<PaystackApiResponse<PaystackDedicatedAccountData>>(
            accountBody, JsonOpts);

        if (accountResult?.Data == null)
            throw new InvalidOperationException("Paystack returned empty dedicated account data.");

        return new PaystackVirtualAccountResult(
            AccountNumber: accountResult.Data.AccountNumber,
            AccountName:   accountResult.Data.AccountName,
            BankName:      accountResult.Data.Bank?.Name ?? "WEMA Bank",
            BankCode:      accountResult.Data.Bank?.Code ?? "035",
            CustomerCode:  customerCode);
    }

    private async Task<string> EnsureCustomerAsync(
        string email, string firstName, string lastName,
        string phone, CancellationToken ct)
    {
        var payload = new
        {
            email      = email,
            first_name = firstName,
            last_name  = lastName,
            phone      = phone
        };

        var response = await _http.PostAsJsonAsync("customer", payload, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        // Paystack returns 200 if customer already exists; treat both as success
        var result = JsonSerializer.Deserialize<PaystackApiResponse<PaystackCustomerData>>(
            body, JsonOpts);

        if (result?.Data?.CustomerCode == null)
            throw new InvalidOperationException($"Could not create Paystack customer: {body}");

        return result.Data.CustomerCode;
    }

    // ── Paystack API response shapes ──────────────────────────────────────────

    private record PaystackApiResponse<T>(
        [property: JsonPropertyName("status")]  bool Status,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")]    T? Data
    );

    private record PaystackTransactionData(
        [property: JsonPropertyName("status")]           string Status,
        [property: JsonPropertyName("reference")]        string Reference,
        [property: JsonPropertyName("amount")]           long Amount,       // kobo
        [property: JsonPropertyName("currency")]         string Currency,
        [property: JsonPropertyName("channel")]          string? Channel,
        [property: JsonPropertyName("gateway_response")] string? GatewayResponse
    );

    private record PaystackCustomerData(
        [property: JsonPropertyName("customer_code")] string CustomerCode,
        [property: JsonPropertyName("email")]         string Email
    );

    private record PaystackDedicatedAccountData(
        [property: JsonPropertyName("account_number")] string AccountNumber,
        [property: JsonPropertyName("account_name")]   string AccountName,
        [property: JsonPropertyName("bank")]           PaystackBankData? Bank
    );

    private record PaystackBankData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("code")] string Code
    );
}
