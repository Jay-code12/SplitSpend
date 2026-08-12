using System.Diagnostics;
using BCrypt.Net;
using SplitSpend.AuthService.Application.DTOs;
using SplitSpend.AuthService.Settings;
using SplitSpend.AuthService.Domain.Entities;
using SplitSpend.AuthService.Domain.Enums;
using SplitSpend.AuthService.Repositories.IAuthRepositores;
using SplitSpend.AuthService.Application.Services.IAuthServices;

namespace SplitSpend.AuthService.Application.Services.AuthServices;

public sealed class AuthService(
    IUserCredentialRepository credentialRepo,
    IRefreshTokenRepository refreshRepo,
    IOtpRepository otpRepo,
    ITokenService tokenService,
    IOtpService otpService,
    IEventPublisher eventPublisher,
    JwtSettings jwtSettings,
    ILogger<AuthService> logger) : IAuthService
{
    // ── Register ──────────────────────────────────────────────────────────────
    public async Task<RegisterResponse> RegisterAsync(
        RegisterRequest request, string correlationId, CancellationToken ct = default)
    {
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");

        // Idempotency check — return early if same key already registered
        if (await credentialRepo.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            logger.LogInformation(
                "Duplicate registration attempt detected. IdempotencyKey={Key} Email={Email}",
                idempotencyKey, request.Email);

            var existing = await credentialRepo.GetByEmailAsync(request.Email, ct);
            return new RegisterResponse(
                existing?.Id.ToString() ?? string.Empty,
                request.Email,
                "Registration already processed.");
        }

        if (await credentialRepo.ExistsByEmailAsync(request.Email, ct))
            throw new AuthException("An account with this email already exists.", 409);

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12);
        var credential = UserCredential.Create(request.Email, passwordHash, idempotencyKey);

        await credentialRepo.AddAsync(credential, ct);
        await credentialRepo.SaveChangesAsync(ct);

        // Send OTP for email verification
        await otpService.SendVerificationOtpAsync(credential.Id, request.Email, ct);

        // Publish user.registered — User Service will create profile and reply with user.created
        await eventPublisher.PublishUserRegisteredAsync(
            credential.Id, request.Email, credential.Role.ToString(), correlationId, ct);

        Activity.Current?.SetTag("auth.action", "register");
        Activity.Current?.SetTag("auth.credential_id", credential.Id.ToString());

        logger.LogInformation(
            "User registered successfully. CredentialId={CredentialId} Email={Email} CorrelationId={CorrelationId}",
            credential.Id, request.Email, correlationId);

        return new RegisterResponse(credential.Id.ToString(), request.Email);
    }

    // ── Login ─────────────────────────────────────────────────────────────────
    public async Task<AuthResponse> LoginAsync(
        LoginRequest request, string ipAddress, string correlationId, CancellationToken ct = default)
    {
        var credential = await credentialRepo.GetByEmailAsync(request.Email, ct)
            ?? throw new AuthException("Invalid email or password.", 401);

        if (credential.IsLockedOut())
            throw new AuthException(
                $"Account is temporarily locked due to too many failed attempts. " +
                $"Please try again after {credential.LockedUntil:HH:mm} UTC.", 423);

        if (!BCrypt.Net.BCrypt.Verify(request.Password, credential.PasswordHash))
        {
            credential.RecordFailedLogin();
            await credentialRepo.SaveChangesAsync(ct);

            logger.LogWarning(
                "Failed login attempt. Email={Email} Attempts={Attempts} CorrelationId={CorrelationId}",
                request.Email, credential.FailedLoginAttempts, correlationId);

            throw new AuthException("Invalid email or password.", 401);
        }

        if (credential.Status == AccountStatus.PendingVerification)
            throw new AuthException("Please verify your email before logging in.", 403);

        if (credential.Status == AccountStatus.Suspended)
            throw new AuthException("Your account has been suspended. Contact support.", 403);

        if (credential.Status == AccountStatus.Deleted)
            throw new AuthException("Account not found.", 404);

        // Generate tokens
        var rawRefreshToken = tokenService.GenerateRefreshToken();
        var refreshTokenHash = tokenService.HashToken(rawRefreshToken);
        var accessToken = tokenService.GenerateAccessToken(
            credential.Id, credential.UserId, credential.Email, credential.Role.ToString());

        var refreshToken = RefreshToken.Create(
            credential.Id,
            refreshTokenHash,
            request.DeviceInfo ?? "Unknown",
            ipAddress,
            expiryDays: jwtSettings.RefreshTokenExpiryDays);

        credential.RecordSuccessfulLogin();
        await refreshRepo.AddAsync(refreshToken, ct);
        await credentialRepo.SaveChangesAsync(ct);

        // Publish user.loggedin
        await eventPublisher.PublishUserLoggedInAsync(
            credential.UserId, credential.Email, ipAddress,
            request.DeviceInfo ?? "Unknown", correlationId, ct);

        Activity.Current?.SetTag("auth.action", "login");
        Activity.Current?.SetTag("auth.credential_id", credential.Id.ToString());
        Activity.Current?.SetTag("user.id", credential.UserId?.ToString() ?? "pending");

        logger.LogInformation(
            "User logged in. CredentialId={CredentialId} UserId={UserId} CorrelationId={CorrelationId}",
            credential.Id, credential.UserId, correlationId);

        return new AuthResponse(
            AccessToken: accessToken,
            RefreshToken: rawRefreshToken,
            ExpiresInSeconds: jwtSettings.AccessTokenExpiryMinutes * 60,
            UserId: credential.UserId?.ToString(),
            Role: credential.Role.ToString());
    }

    // ── Verify Email ──────────────────────────────────────────────────────────
    public async Task<MessageResponse> VerifyEmailAsync(
        VerifyOtpRequest request, string correlationId, CancellationToken ct = default)
    {
        var credential = await credentialRepo.GetByEmailAsync(request.Email, ct)
            ?? throw new AuthException("Account not found.", 404);

        if (credential.IsActive())
            return new MessageResponse("Email is already verified.");

        var otp = await otpRepo.GetLatestValidAsync(
            credential.Id, OtpPurpose.EmailVerification, ct)
            ?? throw new AuthException("OTP is invalid or has expired. Please request a new one.", 400);

        if (otp.Code != request.Code)
            throw new AuthException("Incorrect OTP code.", 400);

        otp.MarkUsed();
        credential.SetStatus(AccountStatus.Active);
        await credentialRepo.SaveChangesAsync(ct);

        // Publish user.verified
        await eventPublisher.PublishUserVerifiedAsync(
            credential.Id, credential.UserId, credential.Email, correlationId, ct);

        logger.LogInformation(
            "Email verified. CredentialId={CredentialId} CorrelationId={CorrelationId}",
            credential.Id, correlationId);

        return new MessageResponse("Email verified successfully. You can now log in.");
    }

    // ── Refresh Token ─────────────────────────────────────────────────────────
    public async Task<AuthResponse> RefreshTokenAsync(
        RefreshTokenRequest request, string ipAddress, string correlationId,
        CancellationToken ct = default)
    {
        var tokenHash = tokenService.HashToken(request.RefreshToken);
        var refreshToken = await refreshRepo.GetByHashAsync(tokenHash, ct)
            ?? throw new AuthException("Invalid or expired refresh token.", 401);

        if (!refreshToken.IsValid())
            throw new AuthException("Refresh token has expired or been revoked.", 401);

        var credential = refreshToken.Credential;

        // Rotate: revoke old token, issue new pair
        refreshToken.Revoke();

        var newRawToken = tokenService.GenerateRefreshToken();
        var newTokenHash = tokenService.HashToken(newRawToken);
        var newRefresh = RefreshToken.Create(
            credential.Id, newTokenHash,
            refreshToken.DeviceInfo, ipAddress,
            expiryDays: jwtSettings.RefreshTokenExpiryDays);

        var accessToken = tokenService.GenerateAccessToken(
            credential.Id, credential.UserId, credential.Email, credential.Role.ToString());

        await refreshRepo.AddAsync(newRefresh, ct);
        await refreshRepo.SaveChangesAsync(ct);

        logger.LogInformation(
            "Token refreshed. CredentialId={CredentialId} CorrelationId={CorrelationId}",
            credential.Id, correlationId);

        return new AuthResponse(
            AccessToken: accessToken,
            RefreshToken: newRawToken,
            ExpiresInSeconds: jwtSettings.AccessTokenExpiryMinutes * 60,
            UserId: credential.UserId?.ToString(),
            Role: credential.Role.ToString());
    }

    // ── Forgot Password ───────────────────────────────────────────────────────
    public async Task<MessageResponse> ForgotPasswordAsync(
        ForgotPasswordRequest request, string correlationId, CancellationToken ct = default)
    {
        var credential = await credentialRepo.GetByEmailAsync(request.Email, ct);

        // Always return success — prevents email enumeration
        if (credential is null || !credential.IsActive())
        {
            logger.LogInformation(
                "Forgot password for unknown/inactive email. Email={Email}", request.Email);
            return new MessageResponse("If this email is registered, a reset code has been sent.");
        }

        await otpService.SendPasswordResetOtpAsync(credential.Id, request.Email, ct);

        logger.LogInformation(
            "Password reset OTP sent. CredentialId={CredentialId} CorrelationId={CorrelationId}",
            credential.Id, correlationId);

        return new MessageResponse("If this email is registered, a reset code has been sent.");
    }

    // ── Reset Password ────────────────────────────────────────────────────────
    public async Task<MessageResponse> ResetPasswordAsync(
        ResetPasswordRequest request, string correlationId, CancellationToken ct = default)
    {
        var credential = await credentialRepo.GetByEmailAsync(request.Email, ct)
            ?? throw new AuthException("Account not found.", 404);

        var otp = await otpRepo.GetLatestValidAsync(
            credential.Id, OtpPurpose.PasswordReset, ct)
            ?? throw new AuthException("OTP is invalid or has expired.", 400);

        if (otp.Code != request.OtpCode)
            throw new AuthException("Incorrect OTP code.", 400);

        var newHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 12);

        otp.MarkUsed();
        credential.UpdatePassword(newHash);

        // Revoke all refresh tokens on password reset for security
        await refreshRepo.RevokeAllForCredentialAsync(credential.Id, ct);
        await credentialRepo.SaveChangesAsync(ct);

        logger.LogInformation(
            "Password reset successful. CredentialId={CredentialId} CorrelationId={CorrelationId}",
            credential.Id, correlationId);

        return new MessageResponse("Password reset successfully. Please log in with your new password.");
    }

    // ── Set PIN ───────────────────────────────────────────────────────────────
    public async Task<MessageResponse> SetPinAsync(
        Guid credentialId, SetPinRequest request, string correlationId, CancellationToken ct = default)
    {
        var credential = await credentialRepo.GetByIdAsync(credentialId, ct)
            ?? throw new AuthException("Credential not found.", 404);

        // Verify identity before allowing PIN change — must supply current password
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPasswordOrOtp, credential.PasswordHash))
        {
            // Try OTP path (user may have reset password and is now setting PIN)
            var otp = await otpRepo.GetLatestValidAsync(
                credential.Id, OtpPurpose.PasswordReset, ct);

            if (otp is null || otp.Code != request.CurrentPasswordOrOtp)
                throw new AuthException("Current password or OTP is incorrect.", 401);

            otp.MarkUsed();
        }

        var pinHash = BCrypt.Net.BCrypt.HashPassword(request.Pin, workFactor: 12);
        credential.SetPin(pinHash);
        await credentialRepo.SaveChangesAsync(ct);

        logger.LogInformation(
            "PIN set/updated. CredentialId={CredentialId} CorrelationId={CorrelationId}",
            credentialId, correlationId);

        return new MessageResponse("PIN set successfully.");
    }

    // ── Logout ────────────────────────────────────────────────────────────────
    public async Task<MessageResponse> LogoutAsync(
        LogoutRequest request, string correlationId, CancellationToken ct = default)
    {
        var tokenHash = tokenService.HashToken(request.RefreshToken);
        var refreshToken = await refreshRepo.GetByHashAsync(tokenHash, ct);

        if (refreshToken is not null && !refreshToken.IsRevoked)
        {
            refreshToken.Revoke();
            await refreshRepo.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "User logged out. CorrelationId={CorrelationId}", correlationId);

        return new MessageResponse("Logged out successfully.");
    }

    // ── Verify PIN (internal use by gateway PIN guard) ────────────────────────
    public async Task<bool> VerifyPinAsync(
        Guid credentialId, string pin, CancellationToken ct = default)
    {
        var credential = await credentialRepo.GetByIdAsync(credentialId, ct);
        if (credential?.PinHash is null) return false;
        return BCrypt.Net.BCrypt.Verify(pin, credential.PinHash);
    }
}
