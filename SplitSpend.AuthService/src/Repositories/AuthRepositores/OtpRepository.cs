using Microsoft.EntityFrameworkCore;
using SplitSpend.AuthService.Data;
using SplitSpend.AuthService.Domain.Entities;
using SplitSpend.AuthService.Domain.Enums;
using SplitSpend.AuthService.Repositories.IAuthRepositores;

namespace SplitSpend.AuthService.Repositories.AuthRepositores
{
    public sealed class OtpRepository(AuthDbContext db) : IOtpRepository
    {
        public Task<OtpRecord?> GetLatestValidAsync(
            Guid credentialId, OtpPurpose purpose, CancellationToken ct = default) =>
            db.OtpRecords
              .Where(x => x.UserCredentialId == credentialId
                       && x.Purpose == purpose
                       && !x.IsUsed
                       && x.ExpiresAt > DateTime.UtcNow)
              .OrderByDescending(x => x.CreatedAt)
              .FirstOrDefaultAsync(ct);

        public async Task AddAsync(OtpRecord otp, CancellationToken ct = default) =>
            await db.OtpRecords.AddAsync(otp, ct);

        public async Task InvalidateAllAsync(
            Guid credentialId, OtpPurpose purpose, CancellationToken ct = default)
        {
            var otps = await db.OtpRecords
                .Where(x => x.UserCredentialId == credentialId
                         && x.Purpose == purpose
                         && !x.IsUsed)
                .ToListAsync(ct);

            foreach (var o in otps) o.MarkUsed();
        }

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            db.SaveChangesAsync(ct);
    }

}
