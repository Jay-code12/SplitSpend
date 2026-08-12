using BudgetService.Application.Events;
using BudgetService.Application.Services;
using BudgetService.Infrastructure.Messaging;

namespace BudgetService.Consumers;

// ── wallet.budget.transfer.completed ─────────────────────────────────────────
/// <summary>
/// Wallet confirmed the Main → Budget transfer for a new budget.
/// Activate the budget and emit budget.activated so Notification Service can alert the user.
/// </summary>
public class WalletBudgetTransferCompletedConsumer
    : KafkaConsumerBase<WalletBudgetTransferCompletedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletBudgetTransferCompletedConsumer(
        IConfiguration config,
        ILogger<WalletBudgetTransferCompletedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletBudgetTransferCompleted,
               "budget-wallet-transfer-completed")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(
        WalletBudgetTransferCompletedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<BudgetApplicationService>();

        await svc.ActivateBudgetAsync(msg.UserId, msg.IdempotencyKey, ct);

        Logger.LogInformation(
            "Budget activated for user {UserId} after wallet transfer confirmed", msg.UserId);
    }
}

// ── wallet.budget.transfer.failed ────────────────────────────────────────────
/// <summary>
/// Wallet could not transfer Main → Budget (insufficient funds at the time of execution,
/// or an internal error). Mark the pending budget as Failed.
/// </summary>
public class WalletBudgetTransferFailedConsumer
    : KafkaConsumerBase<WalletBudgetTransferFailedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletBudgetTransferFailedConsumer(
        IConfiguration config,
        ILogger<WalletBudgetTransferFailedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletBudgetTransferFailed,
               "budget-wallet-transfer-failed")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(
        WalletBudgetTransferFailedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<BudgetApplicationService>();

        await svc.MarkBudgetFailedAsync(msg.UserId, msg.Reason, msg.IdempotencyKey, ct);

        Logger.LogWarning(
            "Budget marked Failed for user {UserId}: {Reason}", msg.UserId, msg.Reason);
    }
}

// ── wallet.budget.debited ─────────────────────────────────────────────────────
/// <summary>
/// A spend was debited from BudgetBalance.
/// Distribute the amount across active budgets in FIFO order and update daily spend tracking.
///
/// CRITICAL: This service only subscribes to wallet.budget.debited — NOT wallet.main.debited.
/// This is the entire reason the two events exist as separate topics.
/// If a user makes an external bank transfer, wallet.main.debited fires — Budget Service
/// never sees it, so spend tracking is never polluted with non-budget transactions.
/// </summary>
public class WalletBudgetDebitedConsumer
    : KafkaConsumerBase<WalletBudgetDebitedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletBudgetDebitedConsumer(
        IConfiguration config,
        ILogger<WalletBudgetDebitedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletBudgetDebited, "budget-wallet-budget-debited")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(WalletBudgetDebitedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<BudgetApplicationService>();

        await svc.RecordBudgetSpendAsync(msg.UserId, msg.Amount, msg.IdempotencyKey, ct);

        Logger.LogInformation(
            "Budget spend ₦{Amount} recorded for user {UserId}", msg.Amount, msg.UserId);
    }
}
