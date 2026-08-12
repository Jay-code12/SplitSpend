using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Migrations
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

            // ── VirtualAccounts ───────────────────────────────────────────────
            mb.CreateTable(
                name: "VirtualAccounts",
                columns: t => new
                {
                    Id                   = t.Column<Guid>(nullable: false),
                    UserId               = t.Column<Guid>(nullable: false),
                    AccountNumber        = t.Column<string>(maxLength: 20, nullable: false),
                    AccountName          = t.Column<string>(maxLength: 200, nullable: false),
                    BankName             = t.Column<string>(maxLength: 100, nullable: false),
                    BankCode             = t.Column<string>(maxLength: 10, nullable: false),
                    PaystackCustomerCode = t.Column<string>(maxLength: 100, nullable: false),
                    IsActive             = t.Column<bool>(nullable: false),
                    CreatedAt            = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_VirtualAccounts", x => x.Id));

            mb.CreateIndex("IX_VirtualAccounts_UserId",
                "VirtualAccounts", "UserId", unique: true);
            mb.CreateIndex("IX_VirtualAccounts_AccountNumber",
                "VirtualAccounts", "AccountNumber", unique: true);
            mb.CreateIndex("IX_VirtualAccounts_PaystackCustomerCode",
                "VirtualAccounts", "PaystackCustomerCode", unique: true);

            // ── PaymentLogs ───────────────────────────────────────────────────
            mb.CreateTable(
                name: "PaymentLogs",
                columns: t => new
                {
                    Id                    = t.Column<Guid>(nullable: false),
                    UserId                = t.Column<Guid>(nullable: false),
                    Amount                = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency              = t.Column<string>(maxLength: 3, nullable: false),
                    Status                = t.Column<string>(nullable: false),
                    Type                  = t.Column<string>(nullable: false),
                    PaystackReference     = t.Column<string>(maxLength: 100, nullable: false),
                    PaystackTransactionId = t.Column<string>(maxLength: 100, nullable: false),
                    Channel               = t.Column<string>(maxLength: 50, nullable: true),
                    GatewayResponse       = t.Column<string>(maxLength: 200, nullable: true),
                    RawWebhookPayload     = t.Column<string>(type: "nvarchar(max)", nullable: false),
                    IdempotencyKey        = t.Column<string>(maxLength: 256, nullable: false),
                    PaidAt                = t.Column<DateTime>(nullable: false),
                    CreatedAt             = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_PaymentLogs", x => x.Id));

            mb.CreateIndex("IX_PaymentLogs_UserId",
                "PaymentLogs", "UserId");
            mb.CreateIndex("IX_PaymentLogs_PaystackReference",
                "PaymentLogs", "PaystackReference", unique: true);
            mb.CreateIndex("IX_PaymentLogs_IdempotencyKey",
                "PaymentLogs", "IdempotencyKey", unique: true);
            mb.CreateIndex("IX_PaymentLogs_CreatedAt",
                "PaymentLogs", "CreatedAt");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable("PaymentLogs");
            mb.DropTable("VirtualAccounts");
            mb.DropTable("IdempotencyRecords");
        }
    }
}
