using PaymentService.Application.DTOs;
using PaymentService.Application.Events;
using PaymentService.Application.Interfaces;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Data;

namespace PaymentService.Application.Services;

/// <summary>
/// Core service for Payment Service — deposits only.
///
/// Scope (from MVP spec):
///   Payment Service has ONE job: handle Paystack deposit webhooks and emit
///   payment.successful so Wallet Service can credit the user.
///   It does NOT handle vendor payouts, user-to-user payments, or external transfers.
///   Those are handled by Wallet Service (internal) and Transfer Service (Paystack Transfers API).
///
/// Webhook flow:
///   1. POST /api/payments/webhook arrives from Paystack
///   2. Controller verifies HMAC-SHA512 signature (rejects immediately if invalid)
///   3. HandleDepositWebhookAsync called with verified payload
///   4. Idempotency check — skip if already processed (Paystack retries webhooks)
///   5. Resolve userId from virtual account number
///   6. Create PaymentLog (Success or Failed)
///   7. Emit payment.successful or payment.failed
///   8. Wallet Service consumes payment.successful → credits Main Balance
///   9. Transaction Service consumes payment.successful → opens deposit transaction
///
/// Manual verify flow (missed webhooks):
///   1. User or support calls GET /api/payments/verify/{ref}
///   2. Re-queries Paystack API for the charge status
///   3. If success and not yet processed → runs the same deposit flow
///   4. Idempotent — safe to call multiple times
/// </summary>
public class PaymentApplicationService
{
    private readonly IPaymentLogRepository _logs;
    private readonly IVirtualAccountRepository _virtualAccounts;
    private readonly IIdempotencyRepository _idempotency;
    private readonly IPaystackClient _paystack;
    private readonly IKafkaPublisher _kafka;
    private readonly PaymentDbContext _db;
    private readonly ILogger<PaymentApplicationService> _log;

    public PaymentApplicationService(
        IPaymentLogRepository logs,
        IVirtualAccountRepository virtualAccounts,
        IIdempotencyRepository idempotency,
        IPaystackClient paystack,
        IKafkaPublisher kafka,
        PaymentDbContext db,
        ILogger<PaymentApplicationService> log)
    {
        _logs           = logs;
        _virtualAccounts = virtualAccounts;
        _idempotency    = idempotency;
        _paystack       = paystack;
        _kafka          = kafka;
        _db             = db;
        _log            = log;
    }

    // ── WEBHOOK HANDLER ───────────────────────────────────────────────────────

    /// <summary>
    /// Processes a verified charge.success Paystack webhook.
    /// Signature must be verified by the controller before calling this method.
    ///
    /// Idempotency key = "deposit:{paystackReference}" — Paystack can retry webhooks
    /// multiple times; this ensures we only process each charge once.
    /// </summary>
    public async Task<WebhookAcknowledgement> HandleDepositWebhookAsync(
        PaystackWebhookRequest webhook,
        string rawPayload,
        CancellationToken ct)
    {
        var reference      = webhook.Data.Reference;
        var idempotencyKey = $"deposit:{reference}";

        // ── Idempotency check ─────────────────────────────────────────────────
        if (await _idempotency.ExistsAsync(idempotencyKey, ct))
        {
            _log.LogInformation(
                "Duplicate webhook ignored for reference {Ref}", reference);
            return new WebhookAcknowledgement(false, "Already processed.");
        }

        // ── Amount: Paystack sends kobo, we store/emit in Naira ───────────────
        var amountNaira = webhook.Data.Amount / 100m;

        // ── Resolve user from virtual account ─────────────────────────────────
        // Paystack sends the customer code — we look up the matching VirtualAccount
        Guid userId;
        try
        {
            userId = await ResolveUserIdAsync(
                webhook.Data.Customer.CustomerCode, reference, ct);
        }
        catch (UserResolutionException ex)
        {
            _log.LogError(ex,
                "Cannot resolve user for webhook ref={Ref} customer={Code}",
                reference, webhook.Data.Customer.CustomerCode);

            // Log the failure but still acknowledge the webhook to stop Paystack retrying
            await RecordAndEmitFailureAsync(
                Guid.Empty, amountNaira, reference,
                idempotencyKey, rawPayload,
                $"User resolution failed: {ex.Message}", ct);

            return new WebhookAcknowledgement(false, "User resolution failed.");
        }

        // ── Process success ───────────────────────────────────────────────────
        DateTime? paidAt = null;
        if (webhook.Data.PaidAt != null &&
            DateTime.TryParse(webhook.Data.PaidAt, out var parsedDate))
            paidAt = parsedDate;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var paymentLog = PaymentLog.CreateSuccess(
                userId,
                amountNaira,
                reference,
                webhook.Data.Authorization?.AuthorizationCode ?? reference,
                idempotencyKey,
                rawPayload,
                webhook.Data.Channel,
                webhook.Data.GatewayResponse,
                paidAt);

            await _logs.AddAsync(paymentLog, ct);
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _logs.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Emit — Wallet Service will credit Main Balance
            await _kafka.PublishAsync(KafkaTopics.PaymentSuccessful, new PaymentSuccessfulEvent(
                paymentLog.Id,
                userId,
                amountNaira,
                reference,
                webhook.Data.Channel,
                idempotencyKey,
                DateTime.UtcNow), ct);

            _log.LogInformation(
                "Deposit processed: ₦{Amount} for user {UserId} | ref={Ref} | channel={Channel}",
                amountNaira, userId, reference, webhook.Data.Channel);

            return new WebhookAcknowledgement(true, "Deposit processed successfully.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── MANUAL VERIFY ────────────────────────────────────────────────────────

    /// <summary>
    /// Re-checks a charge directly with Paystack and processes it if it was missed.
    /// Idempotent — safe to call multiple times; duplicate calls return early.
    ///
    /// Use case: user's money left their bank but SplitSpend balance didn't credit.
    /// Support or the user triggers this endpoint to recover without manual intervention.
    /// </summary>
    public async Task<ManualVerifyResponse> VerifyAndProcessAsync(
        string reference, CancellationToken ct)
    {
        var idempotencyKey = $"deposit:{reference}";

        // Check if already processed
        if (await _idempotency.ExistsAsync(idempotencyKey, ct))
        {
            var existingLog = await _logs.GetByReferenceAsync(reference, ct);
            _log.LogInformation("Manual verify: reference {Ref} already processed", reference);
            return new ManualVerifyResponse(
                reference,
                existingLog?.Status.ToString() ?? "Success",
                existingLog?.Amount ?? 0,
                AlreadyProcessed: true);
        }

        // Re-query Paystack
        PaystackVerifyResponse verification;
        try
        {
            verification = await _paystack.VerifyChargeAsync(reference, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual verify: Paystack API call failed for ref {Ref}", reference);
            throw;
        }

        if (verification.Status != "success")
        {
            _log.LogWarning(
                "Manual verify: Paystack reports status={Status} for ref={Ref}",
                verification.Status, reference);
            return new ManualVerifyResponse(
                reference, verification.Status, verification.Amount, AlreadyProcessed: false);
        }

        // Resolve userId — we need the VirtualAccount lookup
        Guid userId;
        try
        {
            // For manual verify we try to find the account via reference lookup
            // In production this would look up via the Paystack API response's customer code
            var account = await _virtualAccounts.GetByAccountNumberAsync(reference, ct);
            if (account == null)
                throw new UserResolutionException(reference);
            userId = account.UserId;
        }
        catch (UserResolutionException ex)
        {
            _log.LogError(ex, "Manual verify: cannot resolve user for ref {Ref}", reference);
            throw;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var paymentLog = PaymentLog.CreateSuccess(
                userId, verification.Amount, reference,
                reference, idempotencyKey,
                $"{{\"source\":\"manual-verify\",\"reference\":\"{reference}\"}}",
                verification.Channel, verification.GatewayResponse);

            await _logs.AddAsync(paymentLog, ct);
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _logs.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.PaymentSuccessful, new PaymentSuccessfulEvent(
                paymentLog.Id, userId, verification.Amount,
                reference, verification.Channel,
                idempotencyKey, DateTime.UtcNow), ct);

            _log.LogInformation(
                "Manual verify processed: ₦{Amount} for user {UserId} ref={Ref}",
                verification.Amount, userId, reference);

            return new ManualVerifyResponse(
                reference, "success", verification.Amount, AlreadyProcessed: false);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── VIRTUAL ACCOUNT PROVISIONING ──────────────────────────────────────────

    /// <summary>
    /// Creates a Paystack customer and dedicated virtual account for a new user.
    /// Called during user registration — each SplitSpend user gets a unique
    /// Nigerian bank account number they can transfer money to.
    ///
    /// Idempotent: if the user already has a virtual account, returns the existing one.
    /// </summary>
    public async Task<VirtualAccountResponse> ProvisionVirtualAccountAsync(
        ProvisionVirtualAccountRequest req, CancellationToken ct)
    {
        // Return existing account if already provisioned
        var existing = await _virtualAccounts.GetByUserIdAsync(req.UserId, ct);
        if (existing != null)
            return MapToVirtualAccountResponse(existing);

        // Create at Paystack
        PaystackVirtualAccountResult result;
        try
        {
            result = await _paystack.CreateVirtualAccountAsync(
                req.Email, req.FirstName, req.LastName, req.Phone, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to provision virtual account for user {UserId}", req.UserId);
            throw;
        }

        var account = VirtualAccount.Create(
            req.UserId,
            result.AccountNumber,
            result.AccountName,
            result.BankName,
            result.BankCode,
            result.CustomerCode);

        await _virtualAccounts.AddAsync(account, ct);
        await _virtualAccounts.SaveChangesAsync(ct);

        _log.LogInformation(
            "Virtual account provisioned for user {UserId}: {AccountNumber} at {Bank}",
            req.UserId, result.AccountNumber, result.BankName);

        return MapToVirtualAccountResponse(account);
    }

    // ── QUERIES ───────────────────────────────────────────────────────────────

    public async Task<List<PaymentLogResponse>> GetPaymentHistoryAsync(
        Guid userId, CancellationToken ct)
    {
        var logs = await _logs.GetByUserIdAsync(userId, ct);
        return logs.Select(MapToLogResponse).ToList();
    }

    public async Task<VirtualAccountResponse> GetVirtualAccountAsync(
        Guid userId, CancellationToken ct)
    {
        var account = await _virtualAccounts.GetByUserIdAsync(userId, ct)
                      ?? throw new VirtualAccountNotFoundException(userId);
        return MapToVirtualAccountResponse(account);
    }

    // ── PRIVATE HELPERS ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a SplitSpend UserId from the Paystack customer code.
    /// The customer code is stored in VirtualAccount.PaystackCustomerCode.
    /// </summary>
    private async Task<Guid> ResolveUserIdAsync(
        string paystackCustomerCode, string reference, CancellationToken ct)
    {
        // Look up the virtual account by Paystack customer code
        // In a real deployment, Paystack also sends dedicated_nuban details
        // which contain the account number — we can look up either way
        var account = await _virtualAccounts
            .GetByCustomerCodeAsync(paystackCustomerCode, ct);

        if (account == null)
            throw new UserResolutionException(paystackCustomerCode);

        return account.UserId;
    }

    private async Task RecordAndEmitFailureAsync(
        Guid userId, decimal amount, string? reference,
        string idempotencyKey, string rawPayload,
        string reason, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var log = PaymentLog.CreateFailed(userId, amount, reference, idempotencyKey, rawPayload);
            await _logs.AddAsync(log, ct);
            await _idempotency.MarkAsync(idempotencyKey, ct);
            await _logs.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await _kafka.PublishAsync(KafkaTopics.PaymentFailed, new PaymentFailedEvent(
                log.Id, userId, amount, reference,
                reason, idempotencyKey, DateTime.UtcNow), ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static VirtualAccountResponse MapToVirtualAccountResponse(VirtualAccount a) => new(
        a.Id, a.UserId, a.AccountNumber, a.AccountName,
        a.BankName, a.BankCode, a.IsActive, a.CreatedAt);

    private static PaymentLogResponse MapToLogResponse(PaymentLog l) => new(
        l.Id, l.UserId, l.Amount, l.Currency,
        l.Status.ToString(), l.PaystackReference,
        l.Channel, l.GatewayResponse, l.PaidAt, l.CreatedAt);
}
