namespace SplitSpend.AuthService.Domain.Enums;

public enum UserRole
{
    User   = 0,
    Vendor = 1,
    Admin  = 2
}

public enum AccountStatus
{
    PendingVerification = 0,
    Active              = 1,
    Suspended           = 2,
    Deleted             = 3
}

public enum OtpPurpose
{
    EmailVerification = 0,
    PasswordReset     = 1
}
