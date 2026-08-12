using SplitSpend.AuthService.Domain.Entities;

namespace SplitSpend.AuthService.Repositories.IAuthRepositores
{
    public interface IRefreshTokenRepository
    {
        Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default);
        Task AddAsync(RefreshToken token, CancellationToken ct = default);
        Task RevokeAllForCredentialAsync(Guid credentialId, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
