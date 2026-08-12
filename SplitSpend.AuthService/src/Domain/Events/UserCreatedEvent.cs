namespace SplitSpend.AuthService.Domain.Events
{

    /// <summary>
    /// Consumed from User Service after it creates the user profile in response
    /// to user.registered. Contains the authoritative UserId to sync back into
    /// the UserCredential record.
    /// Topic: user.created
    /// </summary>
    public sealed record UserCreatedEvent
    {
        public Guid UserId { get; init; }
        public Guid CredentialId { get; init; }
        public string Email { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public string CorrelationId { get; init; } = string.Empty;
    }

}
