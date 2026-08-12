using System.Security.Cryptography;
using SplitSpend.AuthService.Application.Services.IAuthServices;
using SplitSpend.AuthService.Domain.Entities;
using SplitSpend.AuthService.Domain.Enums;
using SplitSpend.AuthService.Repositories;
using SplitSpend.AuthService.Repositories.IAuthRepositores;

namespace SplitSpend.AuthService.Application.Services.AuthServices;

/// <summary>
/// Generates cryptographically secure 6-digit OTPs, persists them to the DB,
/// and (in production) dispatches them via the notification channel.
///
/// In MVP: OTPs are logged at Information level so they are visible in Seq
/// during development without wiring up an email/SMS provider yet.
/// Replace the logger call with a real notification dispatch in production.
/// </summary>
public sealed class OtpService(
    IOtpRepository otpRepo,
    ILogger<OtpService> logger) : IOtpService
{
    public string GenerateOtp()
    {
        // Cryptographically random 6-digit code
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }

    public async Task SendVerificationOtpAsync(
        Guid credentialId, string email, CancellationToken ct = default)
    {
        // Invalidate any existing unused verification OTPs
        await otpRepo.InvalidateAllAsync(credentialId, OtpPurpose.EmailVerification, ct);

        var code = GenerateOtp();
        var otp = OtpRecord.Create(credentialId, code, OtpPurpose.EmailVerification, expiryMinutes: 15);
        await otpRepo.AddAsync(otp, ct);
        await otpRepo.SaveChangesAsync(ct);

        // TODO (production): publish to Notification Service via Kafka
        // For MVP/dev: log so it is visible in Seq without email provider
        logger.LogInformation(
            "Verification OTP generated. Email={Email} Code={Code} ExpiresAt={ExpiresAt} [DEV ONLY — remove in production]",
            email, code, otp.ExpiresAt);
    }

    public async Task SendPasswordResetOtpAsync(
        Guid credentialId, string email, CancellationToken ct = default)
    {
        await otpRepo.InvalidateAllAsync(credentialId, OtpPurpose.PasswordReset, ct);

        var code = GenerateOtp();
        var otp = OtpRecord.Create(credentialId, code, OtpPurpose.PasswordReset, expiryMinutes: 15);
        await otpRepo.AddAsync(otp, ct);
        await otpRepo.SaveChangesAsync(ct);

        logger.LogInformation(
            "Password reset OTP generated. Email={Email} Code={Code} ExpiresAt={ExpiresAt} [DEV ONLY — remove in production]",
            email, code, otp.ExpiresAt);
    }
}
