using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SplitSpend.AuthService.Application.Services.IAuthServices;
using SplitSpend.AuthService.Settings;

namespace SplitSpend.AuthService.Application.Services.AuthServices;

public sealed class TokenService(JwtSettings settings) : ITokenService
{
    private readonly SymmetricSecurityKey _signingKey =
        new(Encoding.UTF8.GetBytes(settings.SecretKey));

    /// <summary>
    /// Generates a signed JWT access token. Short-lived (15 min by default).
    /// Contains: sub (credentialId), userId, email, role claims.
    /// </summary>
    public string GenerateAccessToken(
        Guid credentialId,
        Guid? userId,
        string email,
        string role)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   credentialId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new(ClaimTypes.Role, role)
        };

        if (userId.HasValue)
            claims.Add(new("userId", userId.Value.ToString()));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(settings.AccessTokenExpiryMinutes),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    /// <summary>
    /// Generates a cryptographically random refresh token (Base64URL, 64 bytes).
    /// The raw value is returned to the client; only the hash is stored in DB.
    /// </summary>
    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// SHA-256 hash of a token. Used to store and compare refresh tokens safely.
    /// </summary>
    public string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Validates an access token and extracts claims. Returns null if invalid.
    /// Used by the gateway's JwtAuthMiddleware; Auth Service uses it internally
    /// for token-based operations like set-pin verification.
    /// </summary>
    public (Guid credentialId, Guid? userId, string email, string role)? ValidateAccessToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKey = _signingKey,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);
            var credentialId = Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
            var userIdStr = principal.FindFirstValue("userId");
            var userId = userIdStr is not null ? Guid.Parse(userIdStr) : (Guid?)null;
            var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email) ?? string.Empty;
            var role = principal.FindFirstValue(ClaimTypes.Role) ?? "User";

            return (credentialId, userId, email, role);
        }
        catch
        {
            return null;
        }
    }
}
