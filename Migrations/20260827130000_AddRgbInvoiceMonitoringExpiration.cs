using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using BTCPayServer.Plugins.RgbUtexo.Data;

#nullable disable

namespace BTCPayServer.Plugins.RgbUtexo.Migrations;

[DbContext(typeof(RGBPluginDbContext))]
[Migration("20260827130000_AddRgbInvoiceMonitoringExpiration")]
public partial class AddRgbInvoiceMonitoringExpiration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "MonitoringExpirationTimestamp",
            table: "RGB_Invoices",
            type: "bigint",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MonitoringExpirationTimestamp",
            table: "RGB_Invoices");
    }
}
