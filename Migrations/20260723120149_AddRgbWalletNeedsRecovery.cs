using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.RgbUtexo.Migrations
{
    /// <inheritdoc />
    public partial class AddRgbWalletNeedsRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsRecovery",
                table: "RGB_Wallets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Finding B upgrade quarantine: existing wallets pre-date the fail-closed persistence
            // subsystem, so their Stock durability is unproven. Quarantine every existing row in
            // the same migration transaction — run-once via __EFMigrationsHistory, fail-closed.
            migrationBuilder.Sql("UPDATE \"RGB_Wallets\" SET \"NeedsRecovery\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeedsRecovery",
                table: "RGB_Wallets");
        }
    }
}
