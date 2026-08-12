using Microsoft.EntityFrameworkCore;
using TransactionService.Domain.Entities;
using TransactionService.Domain.Enums;

namespace TransactionService.Infrastructure.Data;

public class TransactionDbContext : DbContext
{
    public TransactionDbContext(DbContextOptions<TransactionDbContext> options) : base(options) { }

    public DbSet<Transaction>       Transactions      => Set<Transaction>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Transaction ───────────────────────────────────────────────────────
        mb.Entity<Transaction>(e =>
        {
            e.ToTable("Transactions");
            e.HasKey(t => t.Id);

            // Query patterns
            e.HasIndex(t => t.UserId);
            e.HasIndex(t => new { t.UserId, t.Type });
            e.HasIndex(t => new { t.UserId, t.Status });
            e.HasIndex(t => t.CreatedAt);
            e.HasIndex(t => t.IdempotencyKey).IsUnique();

            e.Property(t => t.Amount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(t => t.BudgetDebited).HasColumnType("decimal(18,2)");
            e.Property(t => t.MainDebited).HasColumnType("decimal(18,2)");
            e.Property(t => t.Currency).HasMaxLength(3).IsRequired();

            e.Property(t => t.Type).HasConversion<string>().IsRequired();
            e.Property(t => t.Status).HasConversion<string>().IsRequired();
            e.Property(t => t.DebitSource).HasConversion<string>().IsRequired();

            e.Property(t => t.IdempotencyKey).HasMaxLength(256).IsRequired();
            e.Property(t => t.PaystackReference).HasMaxLength(100);
            e.Property(t => t.ExternalTransferId).HasMaxLength(100);
            e.Property(t => t.FailureReason).HasMaxLength(500);
        });

        // ── IdempotencyRecord ─────────────────────────────────────────────────
        mb.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords");
            e.HasKey(r => r.Key);
            e.Property(r => r.Key).HasMaxLength(256);
            e.HasIndex(r => r.CreatedAt);
        });
    }
}

public class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
