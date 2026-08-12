namespace SplitSpend.UserService.Domain.Enums;

public enum UserRole
{
    User   = 0,
    Vendor = 1,
    Admin  = 2
}

public enum UserStatus
{
    Active    = 0,
    Suspended = 1,
    Deleted   = 2
}

public enum KycStatus
{
    NotSubmitted = 0,
    Pending      = 1,
    Verified     = 2,
    Rejected     = 3
}
