using SplitSpend.UserService.Domain.Enums;

namespace SplitSpend.UserService.Domain.Entities;

/// <summary>
/// Core user record. Created in response to user.registered from Auth Service.
/// Owns identity data — name, contact, role, and status.
/// UserId is the authoritative ID shared across the entire platform.
/// </summary>
public sealed class User
{
    public Guid         Id             { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// The credential ID from Auth Service. Used to correlate back on user.created event.
    /// </summary>
    public Guid         CredentialId   { get; private set; }

    public string       FirstName      { get; private set; } = string.Empty;
    public string       LastName       { get; private set; } = string.Empty;
    public string       Email          { get; private set; } = string.Empty;
    public string?      Phone          { get; private set; }
    public UserRole     Role           { get; private set; } = UserRole.User;
    public UserStatus   Status         { get; private set; } = UserStatus.Active;
    public DateTime     CreatedAt      { get; private set; } = DateTime.UtcNow;
    public DateTime     UpdatedAt      { get; private set; } = DateTime.UtcNow;
    public DateTime?    DeletedAt      { get; private set; }

    // Navigation
    public UserProfile?   Profile        { get; private set; }
    public VendorProfile? VendorProfile  { get; private set; }

    private User() { }

    public static User Create(
        Guid   credentialId,
        string email,
        UserRole role = UserRole.User)
    {
        return new User
        {
            CredentialId = credentialId,
            Email        = email.ToLowerInvariant().Trim(),
            Role         = role,
            Profile      = UserProfile.CreateDefault()
        };
    }

    // ── Domain methods ────────────────────────────────────────────────────────

    public void UpdateProfile(
        string  firstName,
        string  lastName,
        string? phone)
    {
        FirstName = firstName.Trim();
        LastName  = lastName.Trim();
        Phone     = phone?.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetRole(UserRole role)
    {
        Role      = role;
        UpdatedAt = DateTime.UtcNow;

        // Auto-create VendorProfile when promoted to Vendor
        if (role == UserRole.Vendor && VendorProfile is null)
            VendorProfile = VendorProfile.CreateDefault(Id);
    }

    public void SoftDelete()
    {
        Status    = UserStatus.Deleted;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Suspend()
    {
        Status    = UserStatus.Suspended;
        UpdatedAt = DateTime.UtcNow;
    }

    public bool IsActive()  => Status == UserStatus.Active;
    public bool IsDeleted() => Status == UserStatus.Deleted;

    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>
/// Extended profile — avatar, bio, date of birth, KYC status.
/// Created alongside the User record with sensible defaults.
/// </summary>
public sealed class UserProfile
{
    public Guid      Id          { get; private set; } = Guid.NewGuid();
    public Guid      UserId      { get; private set; }
    public string?   AvatarUrl   { get; private set; }
    public string?   Bio         { get; private set; }
    public DateTime? DateOfBirth { get; private set; }
    public KycStatus KycStatus   { get; private set; } = KycStatus.NotSubmitted;
    public DateTime  CreatedAt   { get; private set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt   { get; private set; } = DateTime.UtcNow;

    // Navigation
    public User User { get; private set; } = null!;

    private UserProfile() { }

    public static UserProfile CreateDefault() =>
        new() { KycStatus = KycStatus.NotSubmitted };

    public void Update(
        string?   avatarUrl,
        string?   bio,
        DateTime? dateOfBirth)
    {
        AvatarUrl   = avatarUrl;
        Bio         = bio?.Trim();
        DateOfBirth = dateOfBirth;
        UpdatedAt   = DateTime.UtcNow;
    }

    public void SetKycStatus(KycStatus status)
    {
        KycStatus = status;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Vendor-specific details. Only exists when User.Role == Vendor.
/// Created automatically when a user is promoted to the Vendor role.
/// </summary>
public sealed class VendorProfile
{
    public Guid     Id             { get; private set; } = Guid.NewGuid();
    public Guid     UserId         { get; private set; }
    public string   BusinessName   { get; private set; } = string.Empty;
    public string?  BusinessType   { get; private set; }
    public string?  BusinessAddress { get; private set; }
    public bool     IsVerified     { get; private set; }
    public DateTime CreatedAt      { get; private set; } = DateTime.UtcNow;
    public DateTime UpdatedAt      { get; private set; } = DateTime.UtcNow;

    // Navigation
    public User User { get; private set; } = null!;

    private VendorProfile() { }

    public static VendorProfile CreateDefault(Guid userId) =>
        new() { UserId = userId, BusinessName = string.Empty };

    public void Update(
        string  businessName,
        string? businessType,
        string? businessAddress)
    {
        BusinessName    = businessName.Trim();
        BusinessType    = businessType?.Trim();
        BusinessAddress = businessAddress?.Trim();
        UpdatedAt       = DateTime.UtcNow;
    }

    public void Verify()
    {
        IsVerified = true;
        UpdatedAt  = DateTime.UtcNow;
    }
}
