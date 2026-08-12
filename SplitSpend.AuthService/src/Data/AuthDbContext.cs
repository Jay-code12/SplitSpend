using Microsoft.EntityFrameworkCore;
using SplitSpend.AuthService.Domain.Entities;
using SplitSpend.AuthService.Domain.Enums;

namespace SplitSpend.AuthService.Data;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        // ── UserCredential ────────────────────────────────────────────────────
        mb.Entity<UserCredential>(e =>
        {
            e.ToTable("UserCredentials");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id)             .IsRequired();
            e.Property(x => x.Email)           .IsRequired().HasMaxLength(256);
            e.Property(x => x.PasswordHash)    .IsRequired().HasMaxLength(512);
            e.Property(x => x.PinHash)         .HasMaxLength(512);
            e.Property(x => x.Role)            .IsRequired()
                                                .HasConversion<string>()
                                                .HasMaxLength(32);
            e.Property(x => x.Status)          .IsRequired()
                                                .HasConversion<string>()
                                                .HasMaxLength(32);
            e.Property(x => x.IdempotencyKey)  .IsRequired().HasMaxLength(128);
            e.Property(x => x.CreatedAt)       .IsRequired();
            e.Property(x => x.UpdatedAt)       .IsRequired();

            // Unique email — case-insensitive via computed index
            e.HasIndex(x => x.Email)
             .IsUnique()
             .HasDatabaseName("UQ_UserCredentials_Email");

            // Unique idempotency key — prevents duplicate registrations
            e.HasIndex(x => x.IdempotencyKey)
             .IsUnique()
             .HasDatabaseName("UQ_UserCredentials_IdempotencyKey");

            // Non-unique index on UserId for fast lookup after user.created sync
            e.HasIndex(x => x.UserId)
             .HasDatabaseName("IX_UserCredentials_UserId");

            e.HasMany(x => x.RefreshTokens)
             .WithOne(x => x.Credential)
             .HasForeignKey(x => x.UserCredentialId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── RefreshToken ──────────────────────────────────────────────────────
        mb.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);

            e.Property(x => x.TokenHash)    .IsRequired().HasMaxLength(512);
            e.Property(x => x.DeviceInfo)   .HasMaxLength(512);
            e.Property(x => x.IpAddress)    .HasMaxLength(64);
            e.Property(x => x.ExpiresAt)    .IsRequired();
            e.Property(x => x.CreatedAt)    .IsRequired();

            // Index for fast token lookup on refresh
            e.HasIndex(x => x.TokenHash)
             .HasDatabaseName("IX_RefreshTokens_TokenHash");

            // Index for fast cleanup of tokens by credential
            e.HasIndex(x => x.UserCredentialId)
             .HasDatabaseName("IX_RefreshTokens_UserCredentialId");
        });

        // ── OtpRecord ─────────────────────────────────────────────────────────
        mb.Entity<OtpRecord>(e =>
        {
            e.ToTable("OtpRecords");
            e.HasKey(x => x.Id);

            e.Property(x => x.Code)     .IsRequired().HasMaxLength(8);
            e.Property(x => x.Purpose)  .IsRequired()
                                         .HasConversion<string>()
                                         .HasMaxLength(32);
            e.Property(x => x.ExpiresAt).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            e.HasIndex(x => new { x.UserCredentialId, x.Purpose, x.IsUsed })
             .HasDatabaseName("IX_OtpRecords_Credential_Purpose_Used");
        });
    }

    public DbSet<UserCredential> UserCredentials { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    public DbSet<OtpRecord> OtpRecords { get; set; }

}
