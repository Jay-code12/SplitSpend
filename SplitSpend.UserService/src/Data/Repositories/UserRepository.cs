using Microsoft.EntityFrameworkCore;
using SplitSpend.UserService.Domain.Entities;

namespace SplitSpend.UserService.Data.Repositories;

public interface IUserRepository
{
    Task<User?>  GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?>  GetByEmailAsync(string email, CancellationToken ct = default);
    Task<User?>  GetByCredentialIdAsync(Guid credentialId, CancellationToken ct = default);
    Task<bool>   ExistsByEmailAsync(string email, CancellationToken ct = default);
    Task         AddAsync(User user, CancellationToken ct = default);
    Task         SaveChangesAsync(CancellationToken ct = default);

    // Bypass the global query filter for admin/internal operations on deleted users
    Task<User?>  GetByIdIgnoreFilterAsync(Guid id, CancellationToken ct = default);
}

public sealed class UserRepository(UserDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Users
          .Include(u => u.Profile)
          .Include(u => u.VendorProfile)
          .FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        db.Users
          .Include(u => u.Profile)
          .Include(u => u.VendorProfile)
          .FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant().Trim(), ct);

    public Task<User?> GetByCredentialIdAsync(Guid credentialId, CancellationToken ct = default) =>
        db.Users
          .Include(u => u.Profile)
          .Include(u => u.VendorProfile)
          .FirstOrDefaultAsync(u => u.CredentialId == credentialId, ct);

    public Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default) =>
        db.Users
          .AnyAsync(u => u.Email == email.ToLowerInvariant().Trim(), ct);

    public async Task AddAsync(User user, CancellationToken ct = default) =>
        await db.Users.AddAsync(user, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    // IgnoreQueryFilters bypasses the global soft-delete filter
    public Task<User?> GetByIdIgnoreFilterAsync(Guid id, CancellationToken ct = default) =>
        db.Users
          .IgnoreQueryFilters()
          .Include(u => u.Profile)
          .Include(u => u.VendorProfile)
          .FirstOrDefaultAsync(u => u.Id == id, ct);
}
