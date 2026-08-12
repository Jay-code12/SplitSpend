using SplitSpend.UserService.Application.DTOs;
using SplitSpend.UserService.Domain.Events;

namespace SplitSpend.UserService.Application.Interfaces;

public interface IUserService
{
    Task<UserResponse>    GetByIdAsync(Guid userId, CancellationToken ct = default);
    Task<UserResponse>    UpdateAsync(Guid userId, UpdateUserRequest request, string correlationId, CancellationToken ct = default);
    Task<MessageResponse> DeleteAsync(Guid userId, string correlationId, CancellationToken ct = default);
    Task<MessageResponse> AssignRoleAsync(Guid userId, AssignRoleRequest request, string correlationId, CancellationToken ct = default);
    Task<UserResponse>    CreateFromRegistrationAsync(UserRegisteredEvent evt, CancellationToken ct = default);
}

public interface IUserEventPublisher
{
    Task PublishUserCreatedAsync(Guid userId, Guid credentialId, string email, string role, string correlationId, CancellationToken ct = default);
    Task PublishUserUpdatedAsync(Guid userId, string email, string fullName, string? phone, string role, string correlationId, CancellationToken ct = default);
    Task PublishUserDeletedAsync(Guid userId, string email, string correlationId, CancellationToken ct = default);
}
