using MassTransit;
using SplitSpend.AuthService.Domain.Events;
using SplitSpend.AuthService.Repositories;
using SplitSpend.AuthService.Repositories.IAuthRepositores;

namespace SplitSpend.AuthService.Infrastructure.Messaging;

/// <summary>
/// Consumes user.created events published by User Service.
/// When User Service successfully creates a profile in response to user.registered,
/// it publishes user.created containing the authoritative UserId.
/// Auth Service syncs this UserId back into the UserCredential record so that
/// subsequent JWTs carry the correct userId claim.
///
/// Idempotent: syncing the same UserId twice is a no-op.
/// </summary>
public sealed class UserCreatedConsumer(
    IUserCredentialRepository credentialRepo,
    ILogger<UserCreatedConsumer>    logger) : IConsumer<UserCreatedEvent>
{
    public async Task Consume(ConsumeContext<UserCreatedEvent> context)
    {
        var evt = context.Message;

        using var _ = Serilog.Context.LogContext.PushProperty("CorrelationId", evt.CorrelationId);
        using var __ = Serilog.Context.LogContext.PushProperty("EventType", "user.created");

        logger.LogInformation(
            "Consuming user.created. CredentialId={CredentialId} UserId={UserId} CorrelationId={CorrelationId}",
            evt.CredentialId, evt.UserId, evt.CorrelationId);

        var credential = await credentialRepo.GetByIdAsync(evt.CredentialId, context.CancellationToken);

        if (credential is null)
        {
            logger.LogWarning(
                "user.created received for unknown CredentialId={CredentialId} — ignoring.",
                evt.CredentialId);
            return;
        }

        // Idempotency — skip if already synced
        if (credential.UserId.HasValue && credential.UserId.Value == evt.UserId)
        {
            logger.LogInformation(
                "UserId already synced. CredentialId={CredentialId} — skipping.",
                evt.CredentialId);
            return;
        }

        credential.SetUserId(evt.UserId);
        await credentialRepo.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation(
            "UserId synced to credential. CredentialId={CredentialId} UserId={UserId}",
            evt.CredentialId, evt.UserId);
    }
}
