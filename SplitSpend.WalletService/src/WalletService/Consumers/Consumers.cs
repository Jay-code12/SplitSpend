using WalletService.Application.DTOs;
using WalletService.Application.Events;
using WalletService.Application.Services;
using WalletService.Infrastructure.Messaging;

namespace WalletService.Consumers;

// ── vendor.payment.approved ──────────────────────────────────────────────────
/// <summary>
/// Triggered when a payer approves an in-platform payment request.
/// Executes atomic: debit payer (budget-first) + credit recipient Main Balance.
/// </summary>
public class VendorPaymentApprovedConsumer
    : KafkaConsumerBase<VendorPaymentApprovedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public VendorPaymentApprovedConsumer(
        IConfiguration config,
        ILogger<VendorPaymentApprovedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.VendorPaymentApproved, "wallet-vendor-payment-approved")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(VendorPaymentApprovedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WalletApplicationService>();

        await svc.PayAsync(new InPlatformPayRequest(
            msg.PayerUserId,
            msg.RequesterUserId,
            msg.Amount,
            msg.IdempotencyKey,
            msg.PaymentRequestId.ToString()), ct);
    }
}

// ── payment.successful ───────────────────────────────────────────────────────
/// <summary>
/// Triggered when Paystack confirms a deposit to the user's virtual account.
/// Credits the user's Main Balance.
/// </summary>
public class PaymentSuccessfulConsumer
    : KafkaConsumerBase<PaymentSuccessfulEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PaymentSuccessfulConsumer(
        IConfiguration config,
        ILogger<PaymentSuccessfulConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.PaymentSuccessful, "wallet-payment-successful")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(PaymentSuccessfulEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WalletApplicationService>();

        await svc.CreditAsync(new CreditRequest(
            msg.UserId,
            msg.Amount,
            Domain.Enums.BalanceType.Main,
            msg.IdempotencyKey,
            Reference: msg.PaystackReference,
            Description: "Paystack deposit"), ct);
    }
}

// ── budget.created ───────────────────────────────────────────────────────────
/// <summary>
/// Triggered when Budget Service creates a new budget (Status = Pending).
/// Transfers funds from Main Balance → Budget Balance to fund it.
/// On success → wallet.budget.transfer.completed → Budget Service activates the budget.
/// On failure → wallet.budget.transfer.failed → Budget Service marks budget Failed.
/// </summary>
public class BudgetCreatedConsumer
    : KafkaConsumerBase<BudgetCreatedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BudgetCreatedConsumer(
        IConfiguration config,
        ILogger<BudgetCreatedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.BudgetCreated, "wallet-budget-created")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(BudgetCreatedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WalletApplicationService>();

        await svc.InternalTransferAsync(new InternalTransferRequest(
            msg.UserId,
            msg.TotalAmount,
            "MainToBudget",
            msg.IdempotencyKey,
            $"Fund budget {msg.BudgetId}"), ct);
    }
}

// ── budget.daily.expired ─────────────────────────────────────────────────────
/// <summary>
/// CRON-driven. Returns unused daily budget amount back to Main Balance.
/// </summary>
public class BudgetDailyExpiredConsumer
    : KafkaConsumerBase<BudgetDailyExpiredEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BudgetDailyExpiredConsumer(
        IConfiguration config,
        ILogger<BudgetDailyExpiredConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.BudgetDailyExpired, "wallet-budget-daily-expired")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(BudgetDailyExpiredEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WalletApplicationService>();

        await svc.InternalTransferAsync(new InternalTransferRequest(
            msg.UserId,
            msg.UnusedAmount,
            "BudgetToMain",
            msg.IdempotencyKey,
            $"Daily expiry return for budget {msg.BudgetId}"), ct);
    }
}

// ── gift.sent ────────────────────────────────────────────────────────────────
/// <summary>
/// Triggered when a user sends a gift budget to another user.
/// Debits sender's Main Balance, credits receiver's Budget Balance.
/// </summary>
public class GiftSentConsumer
    : KafkaConsumerBase<GiftSentEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GiftSentConsumer> _log;

    public GiftSentConsumer(
        IConfiguration config,
        ILogger<GiftSentConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.GiftSent, "wallet-gift-sent")
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task HandleAsync(GiftSentEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WalletApplicationService>();

        // Step 1: Debit sender Main Balance
        await svc.DebitAsync(new DebitRequest(
            msg.SenderUserId,
            msg.Amount,
            msg.IdempotencyKey + ":sender",
            msg.ReceiverUserId,
            msg.GiftId.ToString(),
            "Gift budget sent"), ct);

        // Step 2: Credit receiver Budget Balance
        await svc.CreditAsync(new CreditRequest(
            msg.ReceiverUserId,
            msg.Amount,
            Domain.Enums.BalanceType.Budget,
            msg.IdempotencyKey + ":receiver",
            msg.SenderUserId,
            msg.GiftId.ToString(),
            "Gift budget received"), ct);

        _log.LogInformation("Gift {GiftId}: {Amount} moved from {Sender} to {Receiver}",
            msg.GiftId, msg.Amount, msg.SenderUserId, msg.ReceiverUserId);
    }
}

// ── budget.cancelled ─────────────────────────────────────────────────────────
/// <summary>
/// Returns remaining cancelled budget balance back to Main Balance.
/// </summary>
public class BudgetCancelledConsumer
    : KafkaConsumerBase<BudgetCancelledEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BudgetCancelledConsumer(
        IConfiguration config,
        ILogger<BudgetCancelledConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.BudgetCancelled, "wallet-budget-cancelled")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(BudgetCancelledEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WalletApplicationService>();

        await svc.InternalTransferAsync(new InternalTransferRequest(
            msg.UserId,
            msg.RemainingAmount,
            "BudgetToMain",
            msg.IdempotencyKey,
            $"Cancelled budget {msg.BudgetId} refund"), ct);
    }
}

// ── transfer.failed ──────────────────────────────────────────────────────────
/// <summary>
/// Paystack external transfer failed. Reverses the pre-debit from Main Balance.
/// </summary>
public class TransferFailedConsumer
    : KafkaConsumerBase<TransferFailedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TransferFailedConsumer(
        IConfiguration config,
        ILogger<TransferFailedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.TransferFailed, "wallet-transfer-failed")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(TransferFailedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WalletApplicationService>();

        await svc.ReverseExternalTransferAsync(
            msg.UserId,
            msg.Amount,
            msg.TransferId.ToString(),
            msg.IdempotencyKey + ":reversal",
            ct);
    }
}
