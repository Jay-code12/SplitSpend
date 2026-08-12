using System.Diagnostics;
using SplitSpend.UserService.Application.DTOs;
using SplitSpend.UserService.Application.Interfaces;
using SplitSpend.UserService.Common;
using SplitSpend.UserService.Data.Repositories;
using SplitSpend.UserService.Domain.Entities;
using SplitSpend.UserService.Domain.Enums;
using SplitSpend.UserService.Domain.Events;

namespace SplitSpend.UserService.Application.Services;

public sealed class UserService(
    IUserRepository      userRepo,
    IUserEventPublisher  eventPublisher,
    ILogger<UserService> logger) : IUserService
{
    // ── Get Profile ───────────────────────────────────────────────────────────
    public async Task<UserResponse> GetByIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        var user = await userRepo.GetByIdAsync(userId, ct)
            ?? throw new UserException("User not found.", 404);

        return MapToResponse(user);
    }

    // ── Create from Registration (triggered by user.registered consumer) ─────
    public async Task<UserResponse> CreateFromRegistrationAsync(
        UserRegisteredEvent evt, CancellationToken ct = default)
    {
        // Idempotency — if a profile already exists for this credential, return it
        var existing = await userRepo.GetByCredentialIdAsync(evt.CredentialId, ct);
        if (existing is not null)
        {
            logger.LogInformation(
                "User profile already exists for CredentialId={CredentialId} — skipping creation.",
                evt.CredentialId);
            return MapToResponse(existing);
        }

        var role = Enum.TryParse<UserRole>(evt.Role, ignoreCase: true, out var parsedRole)
            ? parsedRole
            : UserRole.User;

        var user = User.Create(evt.CredentialId, evt.Email, role);

        await userRepo.AddAsync(user, ct);
        await userRepo.SaveChangesAsync(ct);

        // Publish user.created → Auth Service syncs back the UserId
        await eventPublisher.PublishUserCreatedAsync(
            user.Id, evt.CredentialId, evt.Email,
            role.ToString(), evt.CorrelationId, ct);

        Activity.Current?.SetTag("user.id",            user.Id.ToString());
        Activity.Current?.SetTag("user.credential_id", evt.CredentialId.ToString());

        logger.LogInformation(
            "User profile created. UserId={UserId} CredentialId={CredentialId} Email={Email} CorrelationId={CorrelationId}",
            user.Id, evt.CredentialId, evt.Email, evt.CorrelationId);

        return MapToResponse(user);
    }

    // ── Update ────────────────────────────────────────────────────────────────
    public async Task<UserResponse> UpdateAsync(
        Guid              userId,
        UpdateUserRequest request,
        string            correlationId,
        CancellationToken ct = default)
    {
        var user = await userRepo.GetByIdAsync(userId, ct)
            ?? throw new UserException("User not found.", 404);

        user.UpdateProfile(request.FirstName, request.LastName, request.Phone);

        if (request.Profile is not null)
            user.Profile?.Update(
                request.Profile.AvatarUrl,
                request.Profile.Bio,
                request.Profile.DateOfBirth);

        if (request.VendorProfile is not null && user.Role == UserRole.Vendor)
            user.VendorProfile?.Update(
                request.VendorProfile.BusinessName,
                request.VendorProfile.BusinessType,
                request.VendorProfile.BusinessAddress);

        await userRepo.SaveChangesAsync(ct);

        await eventPublisher.PublishUserUpdatedAsync(
            user.Id, user.Email, user.FullName,
            user.Phone, user.Role.ToString(), correlationId, ct);

        logger.LogInformation(
            "User profile updated. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);

        return MapToResponse(user);
    }

    // ── Soft Delete ───────────────────────────────────────────────────────────
    public async Task<MessageResponse> DeleteAsync(
        Guid userId, string correlationId, CancellationToken ct = default)
    {
        var user = await userRepo.GetByIdAsync(userId, ct)
            ?? throw new UserException("User not found.", 404);

        if (user.IsDeleted())
            return new MessageResponse("User account is already deleted.");

        user.SoftDelete();
        await userRepo.SaveChangesAsync(ct);

        await eventPublisher.PublishUserDeletedAsync(
            user.Id, user.Email, correlationId, ct);

        logger.LogInformation(
            "User soft-deleted. UserId={UserId} CorrelationId={CorrelationId}",
            userId, correlationId);

        return new MessageResponse("User account deleted successfully.");
    }

    // ── Assign Role (Admin only) ───────────────────────────────────────────────
    public async Task<MessageResponse> AssignRoleAsync(
        Guid              userId,
        AssignRoleRequest request,
        string            correlationId,
        CancellationToken ct = default)
    {
        var user = await userRepo.GetByIdAsync(userId, ct)
            ?? throw new UserException("User not found.", 404);

        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
            throw new UserException($"Invalid role: {request.Role}.", 400);

        user.SetRole(role);
        await userRepo.SaveChangesAsync(ct);

        await eventPublisher.PublishUserUpdatedAsync(
            user.Id, user.Email, user.FullName,
            user.Phone, user.Role.ToString(), correlationId, ct);

        logger.LogInformation(
            "Role assigned. UserId={UserId} NewRole={Role} CorrelationId={CorrelationId}",
            userId, role, correlationId);

        return new MessageResponse($"Role updated to {role} successfully.");
    }

    // ── Mapper ────────────────────────────────────────────────────────────────
    private static UserResponse MapToResponse(User user) =>
        new(
            Id:            user.Id,
            Email:         user.Email,
            FirstName:     user.FirstName,
            LastName:      user.LastName,
            FullName:      user.FullName,
            Phone:         user.Phone,
            Role:          user.Role.ToString(),
            Status:        user.Status.ToString(),
            CreatedAt:     user.CreatedAt,
            Profile:       user.Profile is null ? null : new UserProfileResponse(
                AvatarUrl:   user.Profile.AvatarUrl,
                Bio:         user.Profile.Bio,
                DateOfBirth: user.Profile.DateOfBirth,
                KycStatus:   user.Profile.KycStatus.ToString()),
            VendorProfile: user.VendorProfile is null ? null : new VendorProfileResponse(
                BusinessName:    user.VendorProfile.BusinessName,
                BusinessType:    user.VendorProfile.BusinessType,
                BusinessAddress: user.VendorProfile.BusinessAddress,
                IsVerified:      user.VendorProfile.IsVerified));
}
