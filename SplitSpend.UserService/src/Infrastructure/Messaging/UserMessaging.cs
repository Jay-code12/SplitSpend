using MassTransit;
using SplitSpend.UserService.Application.Interfaces;
using SplitSpend.UserService.Application.Services;
using SplitSpend.UserService.Domain.Events;

namespace SplitSpend.UserService.Infrastructure.Messaging;

// ── Publisher ─────────────────────────────────────────────────────────────────

public sealed class KafkaUserEventPublisher(
    ITopicProducer<string, UserCreatedEvent>  createdProducer,
    ITopicProducer<string, UserUpdatedEvent>  updatedProducer,
    ITopicProducer<string, UserDeletedEvent>  deletedProducer,
    ILogger<KafkaUserEventPublisher>          logger) : IUserEventPublisher
{
    public async Task PublishUserCreatedAsync(
        Guid userId, Guid credentialId, string email,
        string role, string correlationId, CancellationToken ct = default)
    {
        var evt = new UserCreatedEvent
        {
            UserId        = userId,
            CredentialId  = credentialId,
            Email         = email,
            Role          = role,
            CreatedAt     = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        await createdProducer.Produce(userId.ToString(), evt, ct);

        logger.LogInformation(
            "Published user.created. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);
    }

    public async Task PublishUserUpdatedAsync(
        Guid userId, string email, string fullName,
        string? phone, string role, string correlationId, CancellationToken ct = default)
    {
        var evt = new UserUpdatedEvent
        {
            UserId        = userId,
            Email         = email,
            FullName      = fullName,
            Phone         = phone,
            Role          = role,
            UpdatedAt     = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        await updatedProducer.Produce(userId.ToString(), evt, ct);

        logger.LogInformation(
            "Published user.updated. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);
    }

    public async Task PublishUserDeletedAsync(
        Guid userId, string email, string correlationId, CancellationToken ct = default)
    {
        var evt = new UserDeletedEvent
        {
            UserId        = userId,
            Email         = email,
            DeletedAt     = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        await deletedProducer.Produce(userId.ToString(), evt, ct);

        logger.LogInformation(
            "Published user.deleted. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);
    }
}

// ── Consumer — user.registered ────────────────────────────────────────────────

/// <summary>
/// Consumes user.registered published by Auth Service on new registration.
/// Creates the User + UserProfile records, then publishes user.created so
/// Auth Service can sync the UserId back into the UserCredential.
///
/// Idempotent: if a profile already exists for the CredentialId, skips creation.
/// Retries 3 times with exponential back-off before dead-lettering.
/// </summary>
public sealed class UserRegisteredConsumer(
    IUserService                   userService,
    ILogger<UserRegisteredConsumer> logger) : IConsumer<UserRegisteredEvent>
{
    public async Task Consume(ConsumeContext<UserRegisteredEvent> context)
    {
        var evt = context.Message;

        using var _ = Serilog.Context.LogContext.PushProperty("CorrelationId", evt.CorrelationId);
        using var __ = Serilog.Context.LogContext.PushProperty("EventType", "user.registered");

        logger.LogInformation(
            "Consuming user.registered. CredentialId={CredentialId} Email={Email} CorrelationId={CorrelationId}",
            evt.CredentialId, evt.Email, evt.CorrelationId);

        await userService.CreateFromRegistrationAsync(evt, context.CancellationToken);
    }
}
