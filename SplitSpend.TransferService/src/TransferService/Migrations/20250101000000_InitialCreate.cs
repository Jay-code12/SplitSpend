using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransferService.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            // ── IdempotencyRecords ────────────────────────────────────────────
            mb.CreateTable(
                name: "IdempotencyRecords",
                columns: t => new
                {
                    Key       = t.Column<string>(maxLength: 256, nullable: false),
                    CreatedAt = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_IdempotencyRecords", x => x.Key));

            mb.CreateIndex("IX_IdempotencyRecords_CreatedAt", "IdempotencyRecords", "CreatedAt");

            // ── BankTransfers ─────────────────────────────────────────────────
            mb.CreateTable(
                name: "BankTransfers",
                columns: t => new
                {
                    Id                    = t.Column<Guid>(nullable: false),
                    UserId                = t.Column<Guid>(nullable: false),
                    RecipientAccountNumber = t.Column<string>(maxLength: 20, nullable: false),
                    RecipientBankCode     = t.Column<string>(maxLength: 10, nullable: false),
                    RecipientBankName     = t.Column<string>(maxLength: 100, nullable: false),
                    RecipientAccountName  = t.Column<string>(maxLength: 200, nullable: false),
                    Amount                = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency              = t.Column<string>(maxLength: 3, nullable: false),
                    Status                = t.Column<string>(nullable: false),
                    PaystackTransferCode  = t.Column<string>(maxLength: 100, nullable: true),
                    PaystackReference     = t.Column<string>(maxLength: 100, nullable: true),
                    PaystackWebhookData   = t.Column<string>(type: "nvarchar(max)", nullable: true),
                    IdempotencyKey        = t.Column<string>(maxLength: 256, nullable: false),
                    FailureReason         = t.Column<string>(maxLength: 500, nullable: true),
                    ProcessingStartedAt   = t.Column<DateTime>(nullable: true),
                    CompletedAt           = t.Column<DateTime>(nullable: true),
                    FailedAt              = t.Column<DateTime>(nullable: true),
                    ReversedAt            = t.Column<DateTime>(nullable: true),
                    CreatedAt             = t.Column<DateTime>(nullable: false),
                    UpdatedAt             = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_BankTransfers", x => x.Id));

            mb.CreateIndex("IX_BankTransfers_UserId", "BankTransfers", "UserId");
            mb.CreateIndex("IX_BankTransfers_PaystackReference", "BankTransfers",
                "PaystackReference", unique: true, filter: "[PaystackReference] IS NOT NULL");
            mb.CreateIndex("IX_BankTransfers_IdempotencyKey", "BankTransfers",
                "IdempotencyKey", unique: true);
            mb.CreateIndex("IX_BankTransfers_Status_ProcessingStartedAt", "BankTransfers",
                new[] { "Status", "ProcessingStartedAt" });

            // ── BankBeneficiaries ─────────────────────────────────────────────
            mb.CreateTable(
                name: "BankBeneficiaries",
                columns: t => new
                {
                    Id            = t.Column<Guid>(nullable: false),
                    UserId        = t.Column<Guid>(nullable: false),
                    AccountNumber = t.Column<string>(maxLength: 20, nullable: false),
                    BankCode      = t.Column<string>(maxLength: 10, nullable: false),
                    BankName      = t.Column<string>(maxLength: 100, nullable: false),
                    AccountName   = t.Column<string>(maxLength: 200, nullable: false),
                    CreatedAt     = t.Column<DateTime>(nullable: false),
                    UpdatedAt     = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_BankBeneficiaries", x => x.Id));

            mb.CreateIndex("IX_BankBeneficiaries_UserId_Account_Bank", "BankBeneficiaries",
                new[] { "UserId", "AccountNumber", "BankCode" }, unique: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable("BankBeneficiaries");
            mb.DropTable("BankTransfers");
            mb.DropTable("IdempotencyRecords");
        }
    }
}
