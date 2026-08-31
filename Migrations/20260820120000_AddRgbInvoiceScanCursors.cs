using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using BTCPayServer.Plugins.RgbUtexo.Data;

#nullable disable

namespace BTCPayServer.Plugins.RgbUtexo.Migrations;

[DbContext(typeof(RGBPluginDbContext))]
[Migration("20260820120000_AddRgbInvoiceScanCursors")]
public partial class AddRgbInvoiceScanCursors : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "InvoiceScanCursor",
            table: "RGB_Wallets",
            type: "text",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "DiscoveryScanCursor",
            table: "RGB_Wallets",
            type: "text",
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "DiscoveryAssetPage",
            table: "RGB_Wallets",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "InvoiceScanCursor", table: "RGB_Wallets");
        migrationBuilder.DropColumn(name: "DiscoveryScanCursor", table: "RGB_Wallets");
        migrationBuilder.DropColumn(name: "DiscoveryAssetPage", table: "RGB_Wallets");
    }
}
