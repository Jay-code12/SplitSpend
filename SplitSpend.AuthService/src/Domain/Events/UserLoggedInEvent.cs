namespace SplitSpend.AuthService.Domain.Events
{

    /// <summary>
    /// Produced on every successful login.
    /// Notification Service consumes this for login-alert notifications.
    /// Topic: user.loggedin
    /// </summary>
    public sealed record UserLoggedInEvent
    {
        public Guid UserId { get; init; }
        public string Email { get; init; } = string.Empty;
        public string IpAddress { get; init; } = string.Empty;
        public string DeviceInfo { get; init; } = string.Empty;
        public DateTime LoggedInAt { get; init; } = DateTime.UtcNow;
        public string CorrelationId { get; init; } = string.Empty;
    }
}
