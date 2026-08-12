using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SplitSpend.AuthService.Application.DTOs;
using SplitSpend.AuthService.Application.Services.IAuthServices;

namespace SplitSpend.AuthService.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController(
    IAuthService            authService,
    ILogger<AuthController> logger) : ControllerBase
{
    private string CorrelationId =>
        HttpContext.Items["X-Correlation-Id"]?.ToString() ?? "unknown";

    private string IpAddress =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // ── POST /api/auth/register ────────────────────────────────────────────────
    /// <summary>
    /// Register a new user account.
    /// Emits user.registered → User Service creates profile → user.created syncs UserId back.
    /// Sends a 6-digit OTP to the provided email for verification.
    /// Supply X-Idempotency-Key header to safely retry without duplicate registration.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    [ProducesResponseType(typeof(RegisterResponse), 201)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 400)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 409)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var req = request with { IdempotencyKey = idempotencyKey ?? request.IdempotencyKey };
        var result = await authService.RegisterAsync(req, CorrelationId, ct);
        return StatusCode(201, result);
    }

    // ── POST /api/auth/login ──────────────────────────────────────────────────
    /// <summary>
    /// Authenticate with email and password.
    /// Returns a JWT access token (15 min) and a refresh token (30 days).
    /// Account is locked for 15 minutes after 5 consecutive failures.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType(typeof(AuthResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 401)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 423)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken ct)
    {
        var result = await authService.LoginAsync(request, IpAddress, CorrelationId, ct);
        return Ok(result);
    }

    // ── POST /api/auth/verify ─────────────────────────────────────────────────
    /// <summary>
    /// Verify email address using the 6-digit OTP sent at registration.
    /// Must be completed before the account can be used for login.
    /// </summary>
    [HttpPost("verify")]
    [ProducesResponseType(typeof(MessageResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 400)]
    public async Task<IActionResult> VerifyEmail(
        [FromBody] VerifyOtpRequest request,
        CancellationToken ct)
    {
        var result = await authService.VerifyEmailAsync(request, CorrelationId, ct);
        return Ok(result);
    }

    // ── POST /api/auth/refresh-token ──────────────────────────────────────────
    /// <summary>
    /// Rotate a refresh token. Returns a new access token + new refresh token.
    /// The old refresh token is immediately revoked (rotation prevents replay attacks).
    /// </summary>
    [HttpPost("refresh-token")]
    [ProducesResponseType(typeof(AuthResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 401)]
    public async Task<IActionResult> RefreshToken(
        [FromBody] RefreshTokenRequest request,
        CancellationToken ct)
    {
        var result = await authService.RefreshTokenAsync(request, IpAddress, CorrelationId, ct);
        return Ok(result);
    }

    // ── POST /api/auth/forgot-password ────────────────────────────────────────
    /// <summary>
    /// Initiate the password reset flow. Sends a 6-digit OTP to the registered email.
    /// Always returns 200 to prevent email enumeration.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth-forgot")]
    [ProducesResponseType(typeof(MessageResponse), 200)]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken ct)
    {
        var result = await authService.ForgotPasswordAsync(request, CorrelationId, ct);
        return Ok(result);
    }

    // ── POST /api/auth/reset-password ─────────────────────────────────────────
    /// <summary>
    /// Complete the password reset using the OTP from forgot-password.
    /// All existing refresh tokens are revoked on successful reset.
    /// </summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(typeof(MessageResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 400)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken ct)
    {
        var result = await authService.ResetPasswordAsync(request, CorrelationId, ct);
        return Ok(result);
    }

    // ── POST /api/auth/set-pin ────────────────────────────────────────────────
    /// <summary>
    /// Set or update the 4-digit transaction PIN.
    /// Requires a valid JWT (authenticated user) and current password confirmation.
    /// The PIN is required by the API Gateway for all /api/transfers/* requests.
    /// </summary>
    [HttpPost("set-pin")]
    [Authorize]
    [ProducesResponseType(typeof(MessageResponse), 200)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 400)]
    [ProducesResponseType(typeof(ServiceErrorResponse), 401)]
    public async Task<IActionResult> SetPin(
        [FromBody] SetPinRequest request,
        CancellationToken ct)
    {
        var credentialId = GetCredentialId();
        if (credentialId is null)
            return Unauthorized(new ServiceErrorResponse(CorrelationId, 401, "Unauthorized", "Invalid token."));

        var result = await authService.SetPinAsync(credentialId.Value, request, CorrelationId, ct);
        return Ok(result);
    }

    // ── POST /api/auth/logout ─────────────────────────────────────────────────
    /// <summary>
    /// Revoke the provided refresh token.
    /// The access token continues to be valid until it naturally expires (15 min).
    /// For immediate access revocation, add the JTI to a deny-list (post-MVP).
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(typeof(MessageResponse), 200)]
    public async Task<IActionResult> Logout(
        [FromBody] LogoutRequest request,
        CancellationToken ct)
    {
        var result = await authService.LogoutAsync(request, CorrelationId, ct);
        return Ok(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Guid? GetCredentialId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
