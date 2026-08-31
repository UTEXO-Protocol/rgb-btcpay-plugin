using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using BTCPayServer.Plugins.RgbUtexo.Data;

#nullable disable

namespace BTCPayServer.Plugins.RgbUtexo.Migrations;

[DbContext(typeof(RGBPluginDbContext))]
[Migration("20260827120000_AddRgbHotInvoiceScanCursor")]
public partial class AddRgbHotInvoiceScanCursor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "HotInvoiceScanCursor",
            table: "RGB_Wallets",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "HotInvoiceScanCursor", table: "RGB_Wallets");
    }
}
