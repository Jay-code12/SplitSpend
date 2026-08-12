using SplitSpend.AuthService.Domain.Enums;

namespace SplitSpend.AuthService.Domain.Entities;

/// <summary>
/// Core identity record. Stores hashed credentials only — never plaintext.
/// UserId is set after User Service publishes user.created in response to user.registered.
/// </summary>
public sealed class UserCredential
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Populated after User Service responds with user.created event.
    /// Null during the brief window between registration and User Service sync.
    /// </summary>
    public Guid?  UserId { get; private set; }

    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string? PinHash { get; private set; }
    public UserRole Role { get; private set; } = UserRole.User;
    public AccountStatus Status { get; private set; } = AccountStatus.PendingVerification;

    // Lockout
    public int    FailedLoginAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }

    // Audit
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; private set; }

    // Idempotency — prevents duplicate registration from retried requests
    public string IdempotencyKey { get; private set; } = string.Empty;

    // Navigation
    public ICollection<RefreshToken> RefreshTokens { get; private set; } = new List<RefreshToken>();

    private UserCredential() { }

    public static UserCredential Create(
        string email,
        string passwordHash,
        string idempotencyKey,
        UserRole role = UserRole.User)
    {
        return new UserCredential
        {
            Email           = email.ToLowerInvariant().Trim(),
            PasswordHash    = passwordHash,
            IdempotencyKey  = idempotencyKey,
            Role            = role
        };
    }

    // ── Domain methods ────────────────────────────────────────────────────────

    public void SetUserId(Guid userId)
    {
        UserId    = userId;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetStatus(AccountStatus status)
    {
        Status    = status;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetPin(string pinHash)
    {
        PinHash   = pinHash;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RecordSuccessfulLogin()
    {
        FailedLoginAttempts = 0;
        LockedUntil         = null;
        LastLoginAt         = DateTime.UtcNow;
        UpdatedAt           = DateTime.UtcNow;
    }

    public void RecordFailedLogin(int maxAttempts = 5, int lockoutMinutes = 15)
    {
        FailedLoginAttempts++;
        UpdatedAt = DateTime.UtcNow;

        if (FailedLoginAttempts >= maxAttempts)
            LockedUntil = DateTime.UtcNow.AddMinutes(lockoutMinutes);
    }

    public bool IsLockedOut() =>
        LockedUntil.HasValue && LockedUntil.Value > DateTime.UtcNow;

    public bool IsActive() => Status == AccountStatus.Active;

    public void UpdatePassword(string newPasswordHash)
    {
        PasswordHash        = newPasswordHash;
        FailedLoginAttempts = 0;
        LockedUntil         = null;
        UpdatedAt           = DateTime.UtcNow;
    }
}

/// <summary>
/// Refresh token record. Stores the token hash — never the raw token value.
/// One credential can have multiple active tokens (multi-device support).
/// </summary>
public sealed class RefreshToken
{
    public Guid   Id                { get; private set; } = Guid.NewGuid();
    public Guid   UserCredentialId  { get; private set; }
    public string TokenHash         { get; private set; } = string.Empty;
    public string DeviceInfo        { get; private set; } = string.Empty;
    public string IpAddress         { get; private set; } = string.Empty;
    public DateTime ExpiresAt       { get; private set; }
    public bool   IsRevoked         { get; private set; }
    public DateTime? RevokedAt      { get; private set; }
    public DateTime CreatedAt       { get; private set; } = DateTime.UtcNow;

    // Navigation
    public UserCredential Credential { get; private set; } = null!;

    private RefreshToken() { }

    public static RefreshToken Create(
        Guid   credentialId,
        string tokenHash,
        string deviceInfo,
        string ipAddress,
        int    expiryDays = 30)
    {
        return new RefreshToken
        {
            UserCredentialId = credentialId,
            TokenHash        = tokenHash,
            DeviceInfo       = deviceInfo,
            IpAddress        = ipAddress,
            ExpiresAt        = DateTime.UtcNow.AddDays(expiryDays)
        };
    }

    public bool IsExpired()  => ExpiresAt <= DateTime.UtcNow;
    public bool IsValid()    => !IsRevoked && !IsExpired();

    public void Revoke()
    {
        IsRevoked = true;
        RevokedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// OTP record used for email verification and password reset.
/// Each OTP is single-use and expires after a configurable window.
/// </summary>
public sealed class OtpRecord
{
    public Guid      Id              { get; private set; } = Guid.NewGuid();
    public Guid      UserCredentialId { get; private set; }
    public string    Code            { get; private set; } = string.Empty;
    public OtpPurpose Purpose        { get; private set; }
    public bool      IsUsed          { get; private set; }
    public DateTime  ExpiresAt       { get; private set; }
    public DateTime  CreatedAt       { get; private set; } = DateTime.UtcNow;

    private OtpRecord() { }

    public static OtpRecord Create(
        Guid       credentialId,
        string     code,
        OtpPurpose purpose,
        int        expiryMinutes = 15)
    {
        return new OtpRecord
        {
            UserCredentialId = credentialId,
            Code             = code,
            Purpose          = purpose,
            ExpiresAt        = DateTime.UtcNow.AddMinutes(expiryMinutes)
        };
    }

    public bool IsValid() => !IsUsed && ExpiresAt > DateTime.UtcNow;

    public void MarkUsed() => IsUsed = true;
}
