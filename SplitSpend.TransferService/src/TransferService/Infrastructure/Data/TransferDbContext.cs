using Microsoft.EntityFrameworkCore;
using TransferService.Domain.Entities;
using TransferService.Domain.Enums;

namespace TransferService.Infrastructure.Data;

public class TransferDbContext : DbContext
{
    public TransferDbContext(DbContextOptions<TransferDbContext> options) : base(options) { }

    public DbSet<BankTransfer>    BankTransfers    => Set<BankTransfer>();
    public DbSet<BankBeneficiary> BankBeneficiaries => Set<BankBeneficiary>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── BankTransfer ──────────────────────────────────────────────────────
        mb.Entity<BankTransfer>(e =>
        {
            e.ToTable("BankTransfers");
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.UserId);
            e.HasIndex(t => t.PaystackReference).IsUnique();
            e.HasIndex(t => t.IdempotencyKey).IsUnique();
            e.HasIndex(t => new { t.Status, t.ProcessingStartedAt }); // for timeout query

            e.Property(t => t.Amount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(t => t.Currency).HasMaxLength(3).IsRequired();
            e.Property(t => t.Status).HasConversion<string>().IsRequired();

            e.Property(t => t.RecipientAccountNumber).HasMaxLength(20).IsRequired();
            e.Property(t => t.RecipientBankCode).HasMaxLength(10).IsRequired();
            e.Property(t => t.RecipientBankName).HasMaxLength(100).IsRequired();
            e.Property(t => t.RecipientAccountName).HasMaxLength(200).IsRequired();

            e.Property(t => t.PaystackTransferCode).HasMaxLength(100);
            e.Property(t => t.PaystackReference).HasMaxLength(100);
            e.Property(t => t.IdempotencyKey).HasMaxLength(256).IsRequired();
            e.Property(t => t.FailureReason).HasMaxLength(500);
            e.Property(t => t.PaystackWebhookData).HasColumnType("nvarchar(max)");
        });

        // ── BankBeneficiary ───────────────────────────────────────────────────
        mb.Entity<BankBeneficiary>(e =>
        {
            e.ToTable("BankBeneficiaries");
            e.HasKey(b => b.Id);
            // One beneficiary per user+account+bank combination
            e.HasIndex(b => new { b.UserId, b.AccountNumber, b.BankCode }).IsUnique();

            e.Property(b => b.AccountNumber).HasMaxLength(20).IsRequired();
            e.Property(b => b.BankCode).HasMaxLength(10).IsRequired();
            e.Property(b => b.BankName).HasMaxLength(100).IsRequired();
            e.Property(b => b.AccountName).HasMaxLength(200).IsRequired();
        });

        // ── IdempotencyRecord ─────────────────────────────────────────────────
        mb.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords");
            e.HasKey(r => r.Key);
            e.Property(r => r.Key).HasMaxLength(256);
            e.HasIndex(r => r.CreatedAt); // for cleanup
        });
    }
}

public class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
