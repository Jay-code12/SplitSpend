namespace SplitSpend.AuthService.Domain.Events
{

    /// <summary>
    /// Produced when a new user completes registration.
    /// User Service consumes this to create the profile record,
    /// then replies with user.created containing the assigned UserId
    /// Topic: user.registered
    /// </summary>
    public sealed record UserRegisteredEvent
    {
        public Guid CredentialId { get; init; }
        public string Email { get; init; } = string.Empty;
        public string Role { get; init; } = "User";
        public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
        public string CorrelationId { get; init; } = string.Empty;
    }
}
