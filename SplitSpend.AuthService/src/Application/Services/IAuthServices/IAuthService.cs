using SplitSpend.AuthService.Application.DTOs;

namespace SplitSpend.AuthService.Application.Services.IAuthServices
{
    public interface IAuthService
    {
        Task<RegisterResponse> RegisterAsync(RegisterRequest request, string correlationId, CancellationToken ct = default);
        Task<AuthResponse> LoginAsync(LoginRequest request, string ipAddress, string correlationId, CancellationToken ct = default);
        Task<MessageResponse> VerifyEmailAsync(VerifyOtpRequest request, string correlationId, CancellationToken ct = default);
        Task<AuthResponse> RefreshTokenAsync(RefreshTokenRequest request, string ipAddress, string correlationId, CancellationToken ct = default);
        Task<MessageResponse> ForgotPasswordAsync(ForgotPasswordRequest request, string correlationId, CancellationToken ct = default);
        Task<MessageResponse> ResetPasswordAsync(ResetPasswordRequest request, string correlationId, CancellationToken ct = default);
        Task<MessageResponse> SetPinAsync(Guid credentialId, SetPinRequest request, string correlationId, CancellationToken ct = default);
        Task<MessageResponse> LogoutAsync(LogoutRequest request, string correlationId, CancellationToken ct = default);
        Task<bool> VerifyPinAsync(Guid credentialId, string pin, CancellationToken ct = default);
    }

}
