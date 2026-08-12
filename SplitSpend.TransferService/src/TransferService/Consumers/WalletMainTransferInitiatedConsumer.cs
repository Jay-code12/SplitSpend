using TransferService.Application.Events;
using TransferService.Application.Services;
using TransferService.Infrastructure.Messaging;

namespace TransferService.Consumers;

/// <summary>
/// Consumes wallet.main.transfer.initiated.
///
/// This is the critical handoff event from Wallet Service:
/// "Main Balance pre-debited — Transfer Service may now call Paystack."
///
/// The consumer matches the event's TransferReference to our PaystackReference field,
/// transitions the transfer to Processing, and initiates the Paystack payout.
///
/// If Paystack rejects immediately, TransferApplicationService emits transfer.failed
/// which triggers Wallet Service to reverse the pre-debit.
/// </summary>
public class WalletMainTransferInitiatedConsumer
    : KafkaConsumerBase<WalletMainTransferInitiatedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WalletMainTransferInitiatedConsumer(
        IConfiguration config,
        ILogger<WalletMainTransferInitiatedConsumer> log,
        IServiceScopeFactory scopeFactory)
        : base(config, log, KafkaTopics.WalletMainTransferInitiated,
               "transfer-wallet-main-transfer-initiated")
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleAsync(
        WalletMainTransferInitiatedEvent msg, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransferApplicationService>();

        Logger.LogInformation(
            "wallet.main.transfer.initiated received: ref={Ref} user={UserId} amount=₦{Amount}",
            msg.TransferReference, msg.UserId, msg.Amount);

        await svc.OnWalletPreDebitAsync(
            msg.TransferReference,
            msg.UserId,
            msg.Amount,
            msg.IdempotencyKey,
            ct);
    }
}
