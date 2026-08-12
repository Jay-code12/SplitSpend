namespace SplitSpend.AuthService.Domain.Events
{

    /// <summary>
    /// Produced when user confirms their email OTP.
    /// Notification Service consumes this to send a welcome message.
    /// Topic: user.verified
    /// </summary>
    public sealed record UserVerifiedEvent
    {
        public Guid UserId { get; init; }
        public Guid CredentialId { get; init; }
        public string Email { get; init; } = string.Empty;
        public DateTime VerifiedAt { get; init; } = DateTime.UtcNow;
        public string CorrelationId { get; init; } = string.Empty;
    }

}
