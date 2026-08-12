using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SplitSpend.UserService.Application.DTOs;
using SplitSpend.UserService.Application.Interfaces;

namespace SplitSpend.UserService.Controllers;

[ApiController]
[Route("api/users")]
[Produces("application/json")]
[Authorize]
public sealed class UsersController(
    IUserService            userService,
    ILogger<UsersController> logger) : ControllerBase
{
    private string CorrelationId =>
        HttpContext.Items["X-Correlation-Id"]?.ToString() ?? "unknown";

    private string? CallerUserId =>
        HttpContext.Request.Headers["X-User-Id"].FirstOrDefault();

    private string? CallerRole =>
        HttpContext.Request.Headers["X-User-Role"].FirstOrDefault();

    // ── GET /api/users/{id} ───────────────────────────────────────────────────
    /// <summary>
    /// Retrieve a user profile by UserId.
    /// Users may only retrieve their own profile; Admin can retrieve any.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(UserResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 403)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 404)]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken ct)
    {
        if (!IsOwnerOrAdmin(id))
            return Forbid();

        var result = await userService.GetByIdAsync(id, ct);
        return Ok(result);
    }

    // ── PUT /api/users/{id} ───────────────────────────────────────────────────
    /// <summary>
    /// Update a user profile.
    /// Users may only update their own profile; Admin can update any.
    /// VendorProfile fields are only applied when the user has the Vendor role.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(UserResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 400)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 403)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 404)]
    public async Task<IActionResult> UpdateUser(
        Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        if (!IsOwnerOrAdmin(id))
            return Forbid();

        var result = await userService.UpdateAsync(id, request, CorrelationId, ct);
        return Ok(result);
    }

    // ── DELETE /api/users/{id} ────────────────────────────────────────────────
    /// <summary>
    /// Soft-delete a user account.
    /// Users may only delete their own account; Admin can delete any.
    /// Publishes user.deleted so Notification Service can send a farewell notification.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(MessageResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 403)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 404)]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
    {
        if (!IsOwnerOrAdmin(id))
            return Forbid();

        var result = await userService.DeleteAsync(id, CorrelationId, ct);
        return Ok(result);
    }

    // ── POST /api/users/{id}/role ─────────────────────────────────────────────
    /// <summary>
    /// Assign a role to a user. Admin only.
    /// Promoting a user to Vendor automatically creates a VendorProfile.
    /// </summary>
    [HttpPost("{id:guid}/role")]
    [ProducesResponseType(typeof(MessageResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 400)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 403)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 404)]
    public async Task<IActionResult> AssignRole(
        Guid id, [FromBody] AssignRoleRequest request, CancellationToken ct)
    {
        if (!string.Equals(CallerRole, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Non-admin attempted role assignment. CallerUserId={Caller} TargetUserId={Target} CorrelationId={CorrelationId}",
                CallerUserId, id, CorrelationId);
            return StatusCode(403, new ServiceErrorResponse(
                CorrelationId, 403, "Forbidden", "Only admins can assign roles."));
        }

        var result = await userService.AssignRoleAsync(id, request, CorrelationId, ct);
        return Ok(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ownership check: the caller's UserId (stamped by gateway) must match
    /// the requested resource ID, OR the caller must be an Admin.
    /// </summary>
    private bool IsOwnerOrAdmin(Guid resourceId) =>
        string.Equals(CallerRole, "Admin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CallerUserId, resourceId.ToString(), StringComparison.OrdinalIgnoreCase);
}
