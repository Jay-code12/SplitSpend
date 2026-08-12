namespace SplitSpend.UserService.Domain.Events;

// ── Events User Service PRODUCES ─────────────────────────────────────────────

/// <summary>
/// Produced after User Service creates a profile in response to user.registered.
/// Auth Service consumes this to sync the UserId back into the UserCredential record.
/// Topic: user.created
/// </summary>
public sealed record UserCreatedEvent
{
    public Guid     UserId        { get; init; }
    public Guid     CredentialId  { get; init; }
    public string   Email         { get; init; } = string.Empty;
    public string   Role          { get; init; } = "User";
    public DateTime CreatedAt     { get; init; } = DateTime.UtcNow;
    public string   CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// Produced when a user's profile is updated.
/// Notification Service consumes this.
/// Topic: user.updated
/// </summary>
public sealed record UserUpdatedEvent
{
    public Guid     UserId        { get; init; }
    public string   Email         { get; init; } = string.Empty;
    public string   FullName      { get; init; } = string.Empty;
    public string?  Phone         { get; init; }
    public string   Role          { get; init; } = string.Empty;
    public DateTime UpdatedAt     { get; init; } = DateTime.UtcNow;
    public string   CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// Produced when a user account is soft-deleted.
/// Notification Service consumes this.
/// Topic: user.deleted
/// </summary>
public sealed record UserDeletedEvent
{
    public Guid     UserId        { get; init; }
    public string   Email         { get; init; } = string.Empty;
    public DateTime DeletedAt     { get; init; } = DateTime.UtcNow;
    public string   CorrelationId { get; init; } = string.Empty;
}

// ── Events User Service CONSUMES ─────────────────────────────────────────────

/// <summary>
/// Consumed from Auth Service after a new user registers.
/// User Service creates the User + UserProfile records in response,
/// then publishes user.created with the new UserId.
/// Topic: user.registered
/// </summary>
public sealed record UserRegisteredEvent
{
    public Guid     CredentialId  { get; init; }
    public string   Email         { get; init; } = string.Empty;
    public string   Role          { get; init; } = "User";
    public DateTime RegisteredAt  { get; init; } = DateTime.UtcNow;
    public string   CorrelationId { get; init; } = string.Empty;
}
