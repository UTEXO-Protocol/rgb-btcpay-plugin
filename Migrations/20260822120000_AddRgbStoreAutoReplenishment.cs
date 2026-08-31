using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using BTCPayServer.Plugins.RgbUtexo.Data;

#nullable disable

namespace BTCPayServer.Plugins.RgbUtexo.Migrations;

[DbContext(typeof(RGBPluginDbContext))]
[Migration("20260822120000_AddRgbStoreAutoReplenishment")]
public partial class AddRgbStoreAutoReplenishment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RGB_StoreAutoReplenishment",
            columns: table => new
            {
                StoreId = table.Column<string>(type: "text", nullable: false),
                Decision = table.Column<int>(type: "integer", nullable: false),
                DecidedForWalletId = table.Column<string>(type: "text", nullable: true),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DecidedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RGB_StoreAutoReplenishment", x => x.StoreId);
            });

        migrationBuilder.CreateTable(
            name: "RGB_StoreNoticeState",
            columns: table => new
            {
                StoreId = table.Column<string>(type: "text", nullable: false),
                NotAuthorizedNoticeSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CapDisabledNoticeSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ConfigOutOfBoundsNoticeSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RGB_StoreNoticeState", x => x.StoreId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RGB_StoreNoticeState");
        migrationBuilder.DropTable(name: "RGB_StoreAutoReplenishment");
    }
}
