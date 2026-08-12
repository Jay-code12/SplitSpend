namespace SplitSpend.AuthService.Application.Services.IAuthServices
{
    public interface ITokenService
    {
        string GenerateAccessToken(Guid credentialId, Guid? userId, string email, string role);
        string GenerateRefreshToken();
        string HashToken(string token);
        (Guid credentialId, Guid? userId, string email, string role)? ValidateAccessToken(string token);
    }
}
