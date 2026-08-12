using SplitSpend.AuthService.Domain.Entities;
using SplitSpend.AuthService.Domain.Enums;

namespace SplitSpend.AuthService.Repositories.IAuthRepositores
{
    public interface IOtpRepository
    {
        Task<OtpRecord?> GetLatestValidAsync(Guid credentialId, OtpPurpose purpose, CancellationToken ct = default);
        Task AddAsync(OtpRecord otp, CancellationToken ct = default);
        Task InvalidateAllAsync(Guid credentialId, OtpPurpose purpose, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
