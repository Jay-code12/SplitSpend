using Microsoft.EntityFrameworkCore;
using WalletService.Domain.Entities;
using WalletService.Domain.Enums;

namespace WalletService.Infrastructure.Data;

public class WalletDbContext : DbContext
{
    public WalletDbContext(DbContextOptions<WalletDbContext> options) : base(options) { }

    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletLedger> WalletLedger => Set<WalletLedger>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Wallet ────────────────────────────────────────────────────────────
        mb.Entity<Wallet>(e =>
        {
            e.ToTable("Wallets");
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.UserId).IsUnique();

            e.Property(w => w.MainBalance)
                .HasColumnType("decimal(18,2)")
                .IsRequired();

            e.Property(w => w.BudgetBalance)
                .HasColumnType("decimal(18,2)")
                .IsRequired();

            e.Property(w => w.Currency)
                .HasMaxLength(3)
                .IsRequired();

            e.Property(w => w.Status)
                .HasConversion<string>()
                .IsRequired();
        });

        // ── WalletLedger ──────────────────────────────────────────────────────
        mb.Entity<WalletLedger>(e =>
        {
            e.ToTable("WalletLedger");
            e.HasKey(l => l.Id);

            e.HasIndex(l => l.UserId);
            e.HasIndex(l => l.WalletId);
            e.HasIndex(l => l.CreatedAt);
            e.HasIndex(l => l.IdempotencyKey).IsUnique();

            e.Property(l => l.Amount)
                .HasColumnType("decimal(18,2)")
                .IsRequired();

            e.Property(l => l.MainBalanceBefore).HasColumnType("decimal(18,2)");
            e.Property(l => l.BudgetBalanceBefore).HasColumnType("decimal(18,2)");
            e.Property(l => l.MainBalanceAfter).HasColumnType("decimal(18,2)");
            e.Property(l => l.BudgetBalanceAfter).HasColumnType("decimal(18,2)");

            e.Property(l => l.EntryType)
                .HasConversion<string>()
                .IsRequired();

            e.Property(l => l.DebitSource)
                .HasConversion<string>()
                .IsRequired(false);

            e.Property(l => l.Currency).HasMaxLength(3);
            e.Property(l => l.TransactionReference).HasMaxLength(256);
            e.Property(l => l.IdempotencyKey).HasMaxLength(256).IsRequired();
            e.Property(l => l.Description).HasMaxLength(512);

            e.HasOne(l => l.Wallet)
                .WithMany()
                .HasForeignKey(l => l.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── IdempotencyRecord ─────────────────────────────────────────────────
        mb.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords");
            e.HasKey(r => r.Key);
            e.Property(r => r.Key).HasMaxLength(256);
            e.HasIndex(r => r.CreatedAt); // for cleanup jobs
        });
    }
}

public class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
