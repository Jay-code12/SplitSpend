using Microsoft.EntityFrameworkCore;
using PaymentService.Domain.Entities;
using PaymentService.Domain.Enums;

namespace PaymentService.Infrastructure.Data;

public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

    public DbSet<PaymentLog>       PaymentLogs      => Set<PaymentLog>();
    public DbSet<VirtualAccount>   VirtualAccounts  => Set<VirtualAccount>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── PaymentLog ────────────────────────────────────────────────────────
        mb.Entity<PaymentLog>(e =>
        {
            e.ToTable("PaymentLogs");
            e.HasKey(p => p.Id);

            e.HasIndex(p => p.UserId);
            e.HasIndex(p => p.PaystackReference).IsUnique();
            e.HasIndex(p => p.IdempotencyKey).IsUnique();
            e.HasIndex(p => p.CreatedAt);

            e.Property(p => p.Amount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(p => p.Currency).HasMaxLength(3).IsRequired();
            e.Property(p => p.Status).HasConversion<string>().IsRequired();
            e.Property(p => p.Type).HasConversion<string>().IsRequired();

            e.Property(p => p.PaystackReference).HasMaxLength(100).IsRequired();
            e.Property(p => p.PaystackTransactionId).HasMaxLength(100).IsRequired();
            e.Property(p => p.IdempotencyKey).HasMaxLength(256).IsRequired();
            e.Property(p => p.Channel).HasMaxLength(50);
            e.Property(p => p.GatewayResponse).HasMaxLength(200);
            e.Property(p => p.RawWebhookPayload).HasColumnType("nvarchar(max)");
        });

        // ── VirtualAccount ────────────────────────────────────────────────────
        mb.Entity<VirtualAccount>(e =>
        {
            e.ToTable("VirtualAccounts");
            e.HasKey(v => v.Id);

            e.HasIndex(v => v.UserId).IsUnique();             // One account per user
            e.HasIndex(v => v.AccountNumber).IsUnique();      // Account numbers are globally unique
            e.HasIndex(v => v.PaystackCustomerCode).IsUnique();

            e.Property(v => v.AccountNumber).HasMaxLength(20).IsRequired();
            e.Property(v => v.AccountName).HasMaxLength(200).IsRequired();
            e.Property(v => v.BankName).HasMaxLength(100).IsRequired();
            e.Property(v => v.BankCode).HasMaxLength(10).IsRequired();
            e.Property(v => v.PaystackCustomerCode).HasMaxLength(100).IsRequired();
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
