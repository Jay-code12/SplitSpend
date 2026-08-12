using Microsoft.EntityFrameworkCore;
using SplitSpend.AuthService.Data;
using SplitSpend.AuthService.Domain.Entities;
using SplitSpend.AuthService.Repositories.IAuthRepositores;

namespace SplitSpend.AuthService.Repositories.AuthRepositores
{
    public sealed class RefreshTokenRepository(AuthDbContext db) : IRefreshTokenRepository
    {
        public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default) =>
            db.RefreshTokens
              .Include(x => x.Credential)
              .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

        public async Task AddAsync(RefreshToken token, CancellationToken ct = default) =>
            await db.RefreshTokens.AddAsync(token, ct);

        public async Task RevokeAllForCredentialAsync(Guid credentialId, CancellationToken ct = default)
        {
            var tokens = await db.RefreshTokens
                .Where(x => x.UserCredentialId == credentialId && !x.IsRevoked)
                .ToListAsync(ct);

            foreach (var t in tokens) t.Revoke();
        }

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            db.SaveChangesAsync(ct);
    }

}
