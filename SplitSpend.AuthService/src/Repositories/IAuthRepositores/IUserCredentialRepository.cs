using SplitSpend.AuthService.Domain.Entities;

namespace SplitSpend.AuthService.Repositories.IAuthRepositores
{
    public interface IUserCredentialRepository
    {
        Task<UserCredential?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<UserCredential?> GetByEmailAsync(string email, CancellationToken ct = default);
        Task<UserCredential?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
        Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default);
        Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct = default);
        Task AddAsync(UserCredential credential, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
