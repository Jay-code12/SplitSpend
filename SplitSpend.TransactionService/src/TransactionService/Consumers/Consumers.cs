using TransactionService.Application.Events;
using TransactionService.Application.Services;
using TransactionService.Infrastructure.Messaging;

namespace TransactionService.Consumers;

// ── vendor.payment.approved ──────────────────────────────────────────────────
public class VendorPaymentApprovedConsumer
    : KafkaConsumerBase<VendorPaymentApprovedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public VendorPaymentApprovedConsumer(
        IConfiguration config, ILogger<VendorPaymentApprovedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.VendorPaymentApproved, "txn-vendor-payment-approved")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(VendorPaymentApprovedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnVendorPaymentApprovedAsync(msg, ct);
    }
}

// ── wallet.budget.debited ────────────────────────────────────────────────────
public class WalletBudgetDebitedConsumer
    : KafkaConsumerBase<WalletBudgetDebitedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletBudgetDebitedConsumer(
        IConfiguration config, ILogger<WalletBudgetDebitedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletBudgetDebited, "txn-wallet-budget-debited")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(WalletBudgetDebitedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnWalletBudgetDebitedAsync(msg, ct);
    }
}

// ── wallet.main.debited ──────────────────────────────────────────────────────
public class WalletMainDebitedConsumer
    : KafkaConsumerBase<WalletMainDebitedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletMainDebitedConsumer(
        IConfiguration config, ILogger<WalletMainDebitedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletMainDebited, "txn-wallet-main-debited")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(WalletMainDebitedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnWalletMainDebitedAsync(msg, ct);
    }
}

// ── wallet.credited ──────────────────────────────────────────────────────────
/// <summary>
/// Closes both Deposit and InPlatformPayment transactions.
/// The application service distinguishes between them via the idempotency key suffix.
/// </summary>
public class WalletCreditedConsumer
    : KafkaConsumerBase<WalletCreditedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletCreditedConsumer(
        IConfiguration config, ILogger<WalletCreditedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletCredited, "txn-wallet-credited")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(WalletCreditedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnWalletCreditedAsync(msg, ct);
    }
}

// ── wallet.insufficient_funds ────────────────────────────────────────────────
public class WalletInsufficientFundsConsumer
    : KafkaConsumerBase<WalletInsufficientFundsEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletInsufficientFundsConsumer(
        IConfiguration config, ILogger<WalletInsufficientFundsConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletInsufficientFunds, "txn-wallet-insufficient-funds")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(WalletInsufficientFundsEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnWalletInsufficientFundsAsync(msg, ct);
    }
}

// ── payment.successful ───────────────────────────────────────────────────────
public class PaymentSuccessfulConsumer
    : KafkaConsumerBase<PaymentSuccessfulEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PaymentSuccessfulConsumer(
        IConfiguration config, ILogger<PaymentSuccessfulConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.PaymentSuccessful, "txn-payment-successful")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(PaymentSuccessfulEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnPaymentSuccessfulAsync(msg, ct);
    }
}

// ── payment.failed ───────────────────────────────────────────────────────────
public class PaymentFailedConsumer
    : KafkaConsumerBase<PaymentFailedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PaymentFailedConsumer(
        IConfiguration config, ILogger<PaymentFailedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.PaymentFailed, "txn-payment-failed")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(PaymentFailedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnPaymentFailedAsync(msg, ct);
    }
}

// ── transfer.created ─────────────────────────────────────────────────────────
public class TransferCreatedConsumer
    : KafkaConsumerBase<TransferCreatedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TransferCreatedConsumer(
        IConfiguration config, ILogger<TransferCreatedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.TransferCreated, "txn-transfer-created")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(TransferCreatedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnTransferCreatedAsync(msg, ct);
    }
}

// ── transfer.completed ───────────────────────────────────────────────────────
public class TransferCompletedConsumer
    : KafkaConsumerBase<TransferCompletedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TransferCompletedConsumer(
        IConfiguration config, ILogger<TransferCompletedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.TransferCompleted, "txn-transfer-completed")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(TransferCompletedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnTransferCompletedAsync(msg, ct);
    }
}

// ── transfer.failed ──────────────────────────────────────────────────────────
public class TransferFailedConsumer
    : KafkaConsumerBase<TransferFailedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TransferFailedConsumer(
        IConfiguration config, ILogger<TransferFailedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.TransferFailed, "txn-transfer-failed")
        => _scopeFactory = scopeFactory;

    protected override async Task HandleAsync(TransferFailedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionApplicationService>();
        await svc.OnTransferFailedAsync(msg, ct);
    }
}
