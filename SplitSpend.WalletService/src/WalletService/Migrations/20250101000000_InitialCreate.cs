using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WalletService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Key       = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_IdempotencyRecords", x => x.Key));

            mb.CreateIndex(
                name: "IX_IdempotencyRecords_CreatedAt",
                table: "IdempotencyRecords",
                column: "CreatedAt");

            mb.CreateTable(
                name: "Wallets",
                columns: table => new
                {
                    Id            = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId        = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MainBalance   = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BudgetBalance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency      = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status        = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt     = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt     = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_Wallets", x => x.Id));

            mb.CreateIndex(
                name: "IX_Wallets_UserId",
                table: "Wallets",
                column: "UserId",
                unique: true);

            mb.CreateTable(
                name: "WalletLedger",
                columns: table => new
                {
                    Id                   = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WalletId             = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId               = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryType            = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DebitSource          = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Amount               = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency             = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    MainBalanceBefore    = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BudgetBalanceBefore  = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MainBalanceAfter     = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BudgetBalanceAfter   = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CounterpartyId       = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TransactionReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IdempotencyKey       = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description          = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedAt            = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalletLedger_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            mb.CreateIndex(name: "IX_WalletLedger_UserId",          table: "WalletLedger", column: "UserId");
            mb.CreateIndex(name: "IX_WalletLedger_WalletId",         table: "WalletLedger", column: "WalletId");
            mb.CreateIndex(name: "IX_WalletLedger_CreatedAt",        table: "WalletLedger", column: "CreatedAt");
            mb.CreateIndex(name: "IX_WalletLedger_IdempotencyKey",   table: "WalletLedger", column: "IdempotencyKey", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable(name: "WalletLedger");
            mb.DropTable(name: "Wallets");
            mb.DropTable(name: "IdempotencyRecords");
        }
    }
}
