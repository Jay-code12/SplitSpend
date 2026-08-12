using MassTransit;
using SplitSpend.AuthService.Application.Services.IAuthServices;
using SplitSpend.AuthService.Domain.Events;

namespace SplitSpend.AuthService.Infrastructure.Messaging;

/// <summary>
/// Publishes auth domain events to Kafka via MassTransit.
/// Each publish is fire-and-forget from the caller's perspective — MassTransit
/// handles retries and serialization internally. The OpenTelemetry MassTransit
/// instrumentation automatically creates child spans and propagates the TraceId.
/// </summary>
public sealed class KafkaEventPublisher(
    ITopicProducer<string, UserRegisteredEvent>  registeredProducer,
    ITopicProducer<string, UserVerifiedEvent>    verifiedProducer,
    ITopicProducer<string, UserLoggedInEvent>    loggedInProducer,
    ILogger<KafkaEventPublisher>                 logger) : IEventPublisher
{
    public async Task PublishUserRegisteredAsync(
        Guid credentialId, string email, string role,
        string correlationId, CancellationToken ct = default)
    {
        var evt = new UserRegisteredEvent
        {
            CredentialId  = credentialId,
            Email         = email,
            Role          = role,
            RegisteredAt  = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        await registeredProducer.Produce(credentialId.ToString(), evt, ct);

        logger.LogInformation(
            "Published user.registered. CredentialId={CredentialId} CorrelationId={CorrelationId}",
            credentialId, correlationId);
    }

    public async Task PublishUserVerifiedAsync(
        Guid credentialId, Guid? userId, string email,
        string correlationId, CancellationToken ct = default)
    {
        var evt = new UserVerifiedEvent
        {
            UserId        = userId ?? Guid.Empty,
            CredentialId  = credentialId,
            Email         = email,
            VerifiedAt    = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        await verifiedProducer.Produce(credentialId.ToString(), evt, ct);

        logger.LogInformation(
            "Published user.verified. CredentialId={CredentialId} CorrelationId={CorrelationId}",
            credentialId, correlationId);
    }

    public async Task PublishUserLoggedInAsync(
        Guid? userId, string email, string ip, string device,
        string correlationId, CancellationToken ct = default)
    {
        var evt = new UserLoggedInEvent
        {
            UserId        = userId ?? Guid.Empty,
            Email         = email,
            IpAddress     = ip,
            DeviceInfo    = device,
            LoggedInAt    = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        await loggedInProducer.Produce((userId ?? Guid.Empty).ToString(), evt, ct);

        logger.LogInformation(
            "Published user.loggedin. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);
    }
}
