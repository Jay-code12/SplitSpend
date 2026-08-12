using Microsoft.EntityFrameworkCore;
using SplitSpend.UserService.Domain.Entities;
using SplitSpend.UserService.Domain.Enums;

namespace SplitSpend.UserService.Data;

public sealed class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<User>          Users          { get; set; } = null!;
    public DbSet<UserProfile>   UserProfiles   { get; set; } = null!;
    public DbSet<VendorProfile> VendorProfiles { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        // ── User ─────────────────────────────────────────────────────────────
        mb.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);

            e.Property(x => x.CredentialId).IsRequired();
            e.Property(x => x.FirstName)   .HasMaxLength(100).HasDefaultValue(string.Empty);
            e.Property(x => x.LastName)    .HasMaxLength(100).HasDefaultValue(string.Empty);
            e.Property(x => x.Email)       .IsRequired().HasMaxLength(256);
            e.Property(x => x.Phone)       .HasMaxLength(32);
            e.Property(x => x.Role)        .IsRequired().HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status)      .IsRequired().HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CreatedAt)   .IsRequired();
            e.Property(x => x.UpdatedAt)   .IsRequired();

            // Unique email
            e.HasIndex(x => x.Email)
             .IsUnique()
             .HasDatabaseName("UQ_Users_Email");

            // Unique CredentialId — one profile per Auth credential
            e.HasIndex(x => x.CredentialId)
             .IsUnique()
             .HasDatabaseName("UQ_Users_CredentialId");

            // Filter deleted users from regular queries
            e.HasQueryFilter(x => x.Status != UserStatus.Deleted);

            // Navigation
            e.HasOne(x => x.Profile)
             .WithOne(x => x.User)
             .HasForeignKey<UserProfile>(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.VendorProfile)
             .WithOne(x => x.User)
             .HasForeignKey<VendorProfile>(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── UserProfile ───────────────────────────────────────────────────────
        mb.Entity<UserProfile>(e =>
        {
            e.ToTable("UserProfiles");
            e.HasKey(x => x.Id);

            e.Property(x => x.AvatarUrl) .HasMaxLength(2048);
            e.Property(x => x.Bio)       .HasMaxLength(500);
            e.Property(x => x.KycStatus) .IsRequired().HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CreatedAt) .IsRequired();
            e.Property(x => x.UpdatedAt) .IsRequired();

            e.HasIndex(x => x.UserId)
             .IsUnique()
             .HasDatabaseName("UQ_UserProfiles_UserId");
        });

        // ── VendorProfile ─────────────────────────────────────────────────────
        mb.Entity<VendorProfile>(e =>
        {
            e.ToTable("VendorProfiles");
            e.HasKey(x => x.Id);

            e.Property(x => x.BusinessName)    .IsRequired().HasMaxLength(200);
            e.Property(x => x.BusinessType)    .HasMaxLength(100);
            e.Property(x => x.BusinessAddress) .HasMaxLength(500);
            e.Property(x => x.CreatedAt)       .IsRequired();
            e.Property(x => x.UpdatedAt)       .IsRequired();

            e.HasIndex(x => x.UserId)
             .IsUnique()
             .HasDatabaseName("UQ_VendorProfiles_UserId");
        });
    }
}
