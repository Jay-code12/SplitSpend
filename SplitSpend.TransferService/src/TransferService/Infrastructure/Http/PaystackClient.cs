using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TransferService.Application.DTOs;
using TransferService.Application.Interfaces;
using TransferService.Domain.Entities;

namespace TransferService.Infrastructure.Http;

/// <summary>
/// Typed HTTP client for all Paystack API calls made by Transfer Service.
///
/// Paystack API base URL: https://api.paystack.co
/// Auth: Authorization: Bearer {SecretKey} on every request
/// Webhook verification: HMAC-SHA512 of raw payload using SecretKey
///
/// All monetary values from Paystack are in KOBO (smallest NGN unit).
/// We divide by 100 to get Naira. We multiply by 100 to send.
/// </summary>
public class PaystackClient : IPaystackClient
{
    private readonly HttpClient _http;
    private readonly string _secretKey;
    private readonly ILogger<PaystackClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PaystackClient(HttpClient http, IConfiguration config, ILogger<PaystackClient> log)
    {
        _http      = http;
        _secretKey = config["Paystack:SecretKey"]
                     ?? throw new InvalidOperationException("Paystack:SecretKey not configured.");
        _log       = log;
    }

    // ── Transfer initiation ───────────────────────────────────────────────────

    public async Task<string> InitiateTransferAsync(
        string accountNumber, string bankCode, string accountName,
        decimal amount, string reference, CancellationToken ct = default)
    {
        // Step 1: Create a transfer recipient
        var recipientCode = await CreateTransferRecipientAsync(
            accountNumber, bankCode, accountName, ct);

        // Step 2: Initiate the transfer using the recipient code
        var payload = new
        {
            source    = "balance",
            amount    = (long)(amount * 100),   // Convert NGN to kobo
            recipient = recipientCode,
            reference = reference,
            reason    = "SplitSpend bank transfer"
        };

        var response = await _http.PostAsJsonAsync("transfer", payload, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("Paystack transfer initiation failed: {Status} {Body}",
                response.StatusCode, body);
            throw new PaystackApiException(
                $"Paystack transfer failed: {body}", (int)response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<PaystackResponse<PaystackTransferResult>>(body, JsonOpts);

        if (result?.Data?.TransferCode == null)
            throw new PaystackApiException("Paystack returned no transfer code.");

        _log.LogInformation(
            "Paystack transfer initiated: code={Code} ref={Ref}",
            result.Data.TransferCode, reference);

        return result.Data.TransferCode;
    }

    private async Task<string> CreateTransferRecipientAsync(
        string accountNumber, string bankCode, string accountName, CancellationToken ct)
    {
        var payload = new
        {
            type           = "nuban",
            name           = accountName,
            account_number = accountNumber,
            bank_code      = bankCode,
            currency       = "NGN"
        };

        var response = await _http.PostAsJsonAsync("transferrecipient", payload, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new PaystackApiException($"Could not create transfer recipient: {body}", (int)response.StatusCode);

        var result = JsonSerializer.Deserialize<PaystackResponse<PaystackRecipientResult>>(body, JsonOpts);

        if (result?.Data?.RecipientCode == null)
            throw new PaystackApiException("Paystack returned no recipient code.");

        return result.Data.RecipientCode;
    }

    // ── Account verification ───────────────────────────────────────────────────

    public async Task<VerifyAccountResponse> VerifyAccountAsync(
        string accountNumber, string bankCode, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"bank/resolve?account_number={accountNumber}&bank_code={bankCode}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("Account verification failed for {Account}/{Bank}: {Body}",
                accountNumber, bankCode, body);
            throw new PaystackApiException(
                $"Account could not be verified: {body}", (int)response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<PaystackResponse<PaystackAccountResult>>(body, JsonOpts);

        if (result?.Data == null)
            throw new PaystackApiException("Paystack returned empty account data.");

        return new VerifyAccountResponse(
            accountNumber,
            result.Data.AccountName,
            bankCode,
            result.Data.BankName ?? string.Empty);
    }

    // ── Bank list ─────────────────────────────────────────────────────────────

    public async Task<List<NigerianBank>> GetBanksAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("bank?currency=NGN&perPage=100", ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new PaystackApiException($"Could not fetch bank list: {body}");

        var result = JsonSerializer.Deserialize<PaystackResponse<List<PaystackBankResult>>>(body, JsonOpts);

        return result?.Data?.Select(b => new NigerianBank(
            b.Name, b.Code, b.LongCode, b.Active)).ToList()
               ?? new List<NigerianBank>();
    }

    // ── Transfer status query ─────────────────────────────────────────────────

    public async Task<string> GetTransferStatusAsync(
        string transferCode, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"transfer/{transferCode}", ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new PaystackApiException($"Could not fetch transfer status: {body}");

        var result = JsonSerializer.Deserialize<PaystackResponse<PaystackTransferResult>>(body, JsonOpts);
        return result?.Data?.Status ?? "unknown";
    }

    // ── HMAC-SHA512 webhook signature verification ─────────────────────────────

    /// <summary>
    /// Verifies the X-Paystack-Signature header on incoming webhooks.
    /// Signature = HMAC-SHA512(rawPayload, secretKey) as lowercase hex.
    /// Must be called before trusting ANY webhook payload.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(signature))
            return false;

        using var hmac  = new HMACSHA512(Encoding.UTF8.GetBytes(_secretKey));
        var hash        = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var computed    = Convert.ToHexString(hash).ToLowerInvariant();

        var isValid = computed == signature.ToLowerInvariant();

        if (!isValid)
            _log.LogWarning(
                "Webhook signature mismatch. Expected={Expected} Got={Got}",
                computed, signature.ToLowerInvariant());

        return isValid;
    }

    // ── Paystack response shapes ──────────────────────────────────────────────

    private record PaystackResponse<T>(
        [property: JsonPropertyName("status")]  bool Status,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")]    T? Data
    );

    private record PaystackTransferResult(
        [property: JsonPropertyName("transfer_code")] string? TransferCode,
        [property: JsonPropertyName("status")]        string? Status,
        [property: JsonPropertyName("reference")]     string? Reference
    );

    private record PaystackRecipientResult(
        [property: JsonPropertyName("recipient_code")] string? RecipientCode,
        [property: JsonPropertyName("type")]           string? Type
    );

    private record PaystackAccountResult(
        [property: JsonPropertyName("account_name")]   string AccountName,
        [property: JsonPropertyName("account_number")] string AccountNumber,
        [property: JsonPropertyName("bank_name")]      string? BankName
    );

    private record PaystackBankResult(
        [property: JsonPropertyName("name")]      string Name,
        [property: JsonPropertyName("code")]      string Code,
        [property: JsonPropertyName("longcode")]  string? LongCode,
        [property: JsonPropertyName("is_deleted")] bool IsDeleted
    )
    {
        public bool Active => !IsDeleted;
    }
}
