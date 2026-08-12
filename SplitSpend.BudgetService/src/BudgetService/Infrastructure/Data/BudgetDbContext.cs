using BudgetService.Domain.Entities;
using BudgetService.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BudgetService.Infrastructure.Data;

public class BudgetDbContext : DbContext
{
    public BudgetDbContext(DbContextOptions<BudgetDbContext> options) : base(options) { }

    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<UserTotalDailyBudget> UserTotalDailyBudgets => Set<UserTotalDailyBudget>();
    public DbSet<DailyBudgetRecord> DailyBudgetRecords => Set<DailyBudgetRecord>();
    public DbSet<GiftBudget> GiftBudgets => Set<GiftBudget>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Budget ────────────────────────────────────────────────────────────
        mb.Entity<Budget>(e =>
        {
            e.ToTable("Budgets");
            e.HasKey(b => b.Id);
            e.HasIndex(b => b.UserId);
            e.HasIndex(b => new { b.UserId, b.Status });
            e.HasIndex(b => b.IdempotencyKey).IsUnique();

            e.Property(b => b.TotalAmount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(b => b.DailyAmount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(b => b.RemainingTotal).HasColumnType("decimal(18,2)").IsRequired();

            e.Property(b => b.Status).HasConversion<string>().IsRequired();
            e.Property(b => b.Source).HasConversion<string>().IsRequired();
            e.Property(b => b.IdempotencyKey).HasMaxLength(256).IsRequired();

            // Private backing field for navigation
            e.Metadata.FindNavigation(nameof(Budget.DailyRecords))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        // ── UserTotalDailyBudget ──────────────────────────────────────────────
        mb.Entity<UserTotalDailyBudget>(e =>
        {
            e.ToTable("UserTotalDailyBudgets");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.UserId, r.Date }).IsUnique(); // One record per user per day

            e.Property(r => r.TotalAllocated).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(r => r.TotalSpent).HasColumnType("decimal(18,2)").IsRequired();
        });

        // ── DailyBudgetRecord ─────────────────────────────────────────────────
        mb.Entity<DailyBudgetRecord>(e =>
        {
            e.ToTable("DailyBudgetRecords");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.BudgetId, r.Date }).IsUnique(); // One record per budget per day
            e.HasIndex(r => new { r.UserId, r.Date });

            e.Property(r => r.AllocatedAmount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(r => r.SpentAmount).HasColumnType("decimal(18,2)").IsRequired();

            e.HasOne(r => r.Budget)
                .WithMany(b => b.DailyRecords)
                .HasForeignKey(r => r.BudgetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── GiftBudget ────────────────────────────────────────────────────────
        mb.Entity<GiftBudget>(e =>
        {
            e.ToTable("GiftBudgets");
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.SenderUserId);
            e.HasIndex(g => g.ReceiverUserId);
            e.HasIndex(g => g.IdempotencyKey).IsUnique();

            e.Property(g => g.Amount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(g => g.Status).HasConversion<string>().IsRequired();
            e.Property(g => g.IdempotencyKey).HasMaxLength(256).IsRequired();
            e.Property(g => g.Message).HasMaxLength(500);
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
