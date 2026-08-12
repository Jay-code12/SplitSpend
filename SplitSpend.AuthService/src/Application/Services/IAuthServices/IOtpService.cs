namespace SplitSpend.AuthService.Application.Services.IAuthServices
{
    public interface IOtpService
    {
        string GenerateOtp();
        Task SendVerificationOtpAsync(Guid credentialId, string email, CancellationToken ct = default);
        Task SendPasswordResetOtpAsync(Guid credentialId, string email, CancellationToken ct = default);
    }
}
