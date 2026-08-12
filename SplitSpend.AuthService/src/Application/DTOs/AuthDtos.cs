namespace SplitSpend.AuthService.Application.DTOs;

// ── Requests ──────────────────────────────────────────────────────────────────

public sealed record RegisterRequest(
    string Email,
    string Password,
    string ConfirmPassword,
    string? IdempotencyKey = null);

public sealed record LoginRequest(
    string Email,
    string Password,
    string? DeviceInfo = null);

public sealed record VerifyOtpRequest(
    string Email,
    string Code);

public sealed record RefreshTokenRequest(
    string RefreshToken);

public sealed record ForgotPasswordRequest(
    string Email);

public sealed record ResetPasswordRequest(
    string Email,
    string OtpCode,
    string NewPassword,
    string ConfirmNewPassword);

public sealed record SetPinRequest(
    string Pin,
    string ConfirmPin,
    string CurrentPasswordOrOtp);

public sealed record VerifyPinRequest(
    string Pin);

public sealed record LogoutRequest(
    string RefreshToken);

// ── Responses ─────────────────────────────────────────────────────────────────

public sealed record AuthResponse(
    string  AccessToken,
    string  RefreshToken,
    int     ExpiresInSeconds,
    string  TokenType    = "Bearer",
    string? UserId       = null,
    string? Role         = null);

public sealed record RegisterResponse(
    string  CredentialId,
    string  Email,
    string  Message      = "Registration successful. Please verify your email.");

public sealed record MessageResponse(string Message);

public sealed record PinStatusResponse(bool HasPin, string Message);

// ── Internal ──────────────────────────────────────────────────────────────────

public sealed record TokenPair(string AccessToken, string RefreshToken);

public sealed record ServiceErrorResponse(
    string TraceId,
    int    Status,
    string Error,
    string Message,
    DateTime At = default)
{
    public DateTime At { get; init; } = At == default ? DateTime.UtcNow : At;
}
