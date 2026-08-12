using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetService.Migrations
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

            // ── Budgets ───────────────────────────────────────────────────────
            mb.CreateTable(
                name: "Budgets",
                columns: t => new
                {
                    Id             = t.Column<Guid>(nullable: false),
                    UserId         = t.Column<Guid>(nullable: false),
                    TotalAmount    = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DailyAmount    = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RemainingTotal = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DurationDays   = t.Column<int>(nullable: false),
                    StartDate      = t.Column<DateTime>(nullable: false),
                    EndDate        = t.Column<DateTime>(nullable: false),
                    Status         = t.Column<string>(nullable: false),
                    Source         = t.Column<string>(nullable: false),
                    GiftSenderId   = t.Column<Guid>(nullable: true),
                    IdempotencyKey = t.Column<string>(maxLength: 256, nullable: false),
                    CreatedAt      = t.Column<DateTime>(nullable: false),
                    UpdatedAt      = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_Budgets", x => x.Id));

            mb.CreateIndex("IX_Budgets_UserId", "Budgets", "UserId");
            mb.CreateIndex("IX_Budgets_UserId_Status", "Budgets",
                new[] { "UserId", "Status" });
            mb.CreateIndex("IX_Budgets_IdempotencyKey", "Budgets",
                "IdempotencyKey", unique: true);

            // ── DailyBudgetRecords ────────────────────────────────────────────
            mb.CreateTable(
                name: "DailyBudgetRecords",
                columns: t => new
                {
                    Id              = t.Column<Guid>(nullable: false),
                    BudgetId        = t.Column<Guid>(nullable: false),
                    UserId          = t.Column<Guid>(nullable: false),
                    Date            = t.Column<DateTime>(nullable: false),
                    AllocatedAmount = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SpentAmount     = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsExpired       = t.Column<bool>(nullable: false),
                    CreatedAt       = t.Column<DateTime>(nullable: false),
                    UpdatedAt       = t.Column<DateTime>(nullable: false)
                },
                constraints: t =>
                {
                    t.PrimaryKey("PK_DailyBudgetRecords", x => x.Id);
                    t.ForeignKey(
                        name: "FK_DailyBudgetRecords_Budgets_BudgetId",
                        column: x => x.BudgetId,
                        principalTable: "Budgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            mb.CreateIndex("IX_DailyBudgetRecords_BudgetId_Date", "DailyBudgetRecords",
                new[] { "BudgetId", "Date" }, unique: true);
            mb.CreateIndex("IX_DailyBudgetRecords_UserId_Date", "DailyBudgetRecords",
                new[] { "UserId", "Date" });

            // ── UserTotalDailyBudgets ─────────────────────────────────────────
            mb.CreateTable(
                name: "UserTotalDailyBudgets",
                columns: t => new
                {
                    Id             = t.Column<Guid>(nullable: false),
                    UserId         = t.Column<Guid>(nullable: false),
                    Date           = t.Column<DateTime>(nullable: false),
                    TotalAllocated = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalSpent     = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedAt      = t.Column<DateTime>(nullable: false),
                    UpdatedAt      = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_UserTotalDailyBudgets", x => x.Id));

            mb.CreateIndex("IX_UserTotalDailyBudgets_UserId_Date", "UserTotalDailyBudgets",
                new[] { "UserId", "Date" }, unique: true);

            // ── GiftBudgets ───────────────────────────────────────────────────
            mb.CreateTable(
                name: "GiftBudgets",
                columns: t => new
                {
                    Id               = t.Column<Guid>(nullable: false),
                    SenderUserId     = t.Column<Guid>(nullable: false),
                    ReceiverUserId   = t.Column<Guid>(nullable: false),
                    Amount           = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DurationDays     = t.Column<int>(nullable: false),
                    Status           = t.Column<string>(nullable: false),
                    ResultingBudgetId = t.Column<Guid>(nullable: true),
                    IdempotencyKey   = t.Column<string>(maxLength: 256, nullable: false),
                    Message          = t.Column<string>(maxLength: 500, nullable: true),
                    CreatedAt        = t.Column<DateTime>(nullable: false),
                    UpdatedAt        = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_GiftBudgets", x => x.Id));

            mb.CreateIndex("IX_GiftBudgets_SenderUserId",   "GiftBudgets", "SenderUserId");
            mb.CreateIndex("IX_GiftBudgets_ReceiverUserId", "GiftBudgets", "ReceiverUserId");
            mb.CreateIndex("IX_GiftBudgets_IdempotencyKey", "GiftBudgets",
                "IdempotencyKey", unique: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable("DailyBudgetRecords");
            mb.DropTable("UserTotalDailyBudgets");
            mb.DropTable("GiftBudgets");
            mb.DropTable("Budgets");
            mb.DropTable("IdempotencyRecords");
        }
    }
}
