using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using BTCPayServer.Plugins.RgbUtexo.Data;

#nullable disable

namespace BTCPayServer.Plugins.RgbUtexo.Migrations;

[DbContext(typeof(RGBPluginDbContext))]
[Migration("20260824120000_AddRgbPricingNoticeMarker")]
public partial class AddRgbPricingNoticeMarker : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PricingCodeHasNoRuleNoticeSentAt",
            table: "RGB_StoreNoticeState",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PricingCodeHasNoRuleNoticeSentAt",
            table: "RGB_StoreNoticeState");
    }
}
