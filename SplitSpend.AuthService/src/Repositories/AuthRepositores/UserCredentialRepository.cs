using Microsoft.EntityFrameworkCore;
using SplitSpend.AuthService.Data;
using SplitSpend.AuthService.Domain.Entities;
using SplitSpend.AuthService.Repositories.IAuthRepositores;

namespace SplitSpend.AuthService.Repositories.AuthRepositores
{

    // ── Implementations ───────────────────────────────────────────────────────────

    public sealed class UserCredentialRepository(AuthDbContext db) : IUserCredentialRepository
    {
        public Task<UserCredential?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            db.UserCredentials
              .Include(x => x.RefreshTokens)
              .FirstOrDefaultAsync(x => x.Id == id, ct);

        public Task<UserCredential?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            db.UserCredentials
              .Include(x => x.RefreshTokens)
              .FirstOrDefaultAsync(x => x.Email == email.ToLowerInvariant().Trim(), ct);

        public Task<UserCredential?> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
            db.UserCredentials
              .FirstOrDefaultAsync(x => x.UserId == userId, ct);

        public Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default) =>
            db.UserCredentials
              .AnyAsync(x => x.Email == email.ToLowerInvariant().Trim(), ct);

        public Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct = default) =>
            db.UserCredentials
              .AnyAsync(x => x.IdempotencyKey == key, ct);

        public async Task AddAsync(UserCredential credential, CancellationToken ct = default) =>
            await db.UserCredentials.AddAsync(credential, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            db.SaveChangesAsync(ct);
    }
}
