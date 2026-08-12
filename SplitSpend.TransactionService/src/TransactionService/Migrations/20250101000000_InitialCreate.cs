using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactionService.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "IdempotencyRecords",
                columns: t => new
                {
                    Key       = t.Column<string>(maxLength: 256, nullable: false),
                    CreatedAt = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_IdempotencyRecords", x => x.Key));

            mb.CreateIndex("IX_IdempotencyRecords_CreatedAt", "IdempotencyRecords", "CreatedAt");

            mb.CreateTable(
                name: "Transactions",
                columns: t => new
                {
                    Id                  = t.Column<Guid>(nullable: false),
                    UserId              = t.Column<Guid>(nullable: false),
                    Type                = t.Column<string>(nullable: false),
                    Status              = t.Column<string>(nullable: false),
                    Amount              = t.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency            = t.Column<string>(maxLength: 3, nullable: false),
                    DebitSource         = t.Column<string>(nullable: false),
                    BudgetDebited       = t.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MainDebited         = t.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CounterpartyUserId  = t.Column<Guid>(nullable: true),
                    PaystackReference   = t.Column<string>(maxLength: 100, nullable: true),
                    ExternalTransferId  = t.Column<string>(maxLength: 100, nullable: true),
                    IdempotencyKey      = t.Column<string>(maxLength: 256, nullable: false),
                    FailureReason       = t.Column<string>(maxLength: 500, nullable: true),
                    ProcessingStartedAt = t.Column<DateTime>(nullable: true),
                    CompletedAt         = t.Column<DateTime>(nullable: true),
                    FailedAt            = t.Column<DateTime>(nullable: true),
                    CreatedAt           = t.Column<DateTime>(nullable: false),
                    UpdatedAt           = t.Column<DateTime>(nullable: false)
                },
                constraints: t => t.PrimaryKey("PK_Transactions", x => x.Id));

            mb.CreateIndex("IX_Transactions_UserId",             "Transactions", "UserId");
            mb.CreateIndex("IX_Transactions_UserId_Type",        "Transactions", new[] { "UserId", "Type" });
            mb.CreateIndex("IX_Transactions_UserId_Status",      "Transactions", new[] { "UserId", "Status" });
            mb.CreateIndex("IX_Transactions_CreatedAt",          "Transactions", "CreatedAt");
            mb.CreateIndex("IX_Transactions_IdempotencyKey",     "Transactions", "IdempotencyKey", unique: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable("Transactions");
            mb.DropTable("IdempotencyRecords");
        }
    }
}
