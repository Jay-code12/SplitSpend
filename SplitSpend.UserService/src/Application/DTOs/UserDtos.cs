using SplitSpend.UserService.Domain.Enums;

namespace SplitSpend.UserService.Application.DTOs;

// ── Responses ─────────────────────────────────────────────────────────────────

public sealed record UserResponse(
    Guid        Id,
    string      Email,
    string      FirstName,
    string      LastName,
    string      FullName,
    string?     Phone,
    string      Role,
    string      Status,
    DateTime    CreatedAt,
    UserProfileResponse?  Profile,
    VendorProfileResponse? VendorProfile);

public sealed record UserProfileResponse(
    string?   AvatarUrl,
    string?   Bio,
    DateTime? DateOfBirth,
    string    KycStatus);

public sealed record VendorProfileResponse(
    string  BusinessName,
    string? BusinessType,
    string? BusinessAddress,
    bool    IsVerified);

public sealed record MessageResponse(string Message);

public sealed record ServiceErrorResponse(
    string   TraceId,
    int      Status,
    string   Error,
    string   Message,
    DateTime At = default)
{
    public DateTime At { get; init; } = At == default ? DateTime.UtcNow : At;
}

// ── Requests ──────────────────────────────────────────────────────────────────

public sealed record UpdateUserRequest(
    string  FirstName,
    string  LastName,
    string? Phone,
    UpdateProfileRequest? Profile,
    UpdateVendorProfileRequest? VendorProfile);

public sealed record UpdateProfileRequest(
    string?   AvatarUrl,
    string?   Bio,
    DateTime? DateOfBirth);

public sealed record UpdateVendorProfileRequest(
    string  BusinessName,
    string? BusinessType,
    string? BusinessAddress);

public sealed record AssignRoleRequest(string Role);
