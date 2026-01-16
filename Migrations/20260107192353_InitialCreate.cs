using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.RGB.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RGB_Wallets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    XpubVanilla = table.Column<string>(type: "text", nullable: false),
                    XpubColored = table.Column<string>(type: "text", nullable: false),
                    MasterFingerprint = table.Column<string>(type: "text", nullable: false),
                    EncryptedMnemonic = table.Column<string>(type: "text", nullable: false),
                    Network = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RGB_Wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RGB_Assets",
                columns: table => new
                {
                    AssetId = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    Ticker = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Precision = table.Column<int>(type: "integer", nullable: false),
                    IssuedSupply = table.Column<long>(type: "bigint", nullable: false),
                    AcceptForPayment = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RGB_Assets", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_RGB_Assets_RGB_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "RGB_Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RGB_Invoices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    BtcPayInvoiceId = table.Column<string>(type: "text", nullable: true),
                    Invoice = table.Column<string>(type: "text", nullable: false),
                    RecipientId = table.Column<string>(type: "text", nullable: false),
                    AssetId = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<long>(type: "bigint", nullable: true),
                    ReceivedAmount = table.Column<long>(type: "bigint", nullable: true),
                    ExpirationTimestamp = table.Column<long>(type: "bigint", nullable: true),
                    BatchTransferIdx = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IsBlind = table.Column<bool>(type: "boolean", nullable: false),
                    Txid = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RGB_Invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RGB_Invoices_RGB_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "RGB_Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RGB_Assets_WalletId",
                table: "RGB_Assets",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_RGB_Invoices_BtcPayInvoiceId",
                table: "RGB_Invoices",
                column: "BtcPayInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_RGB_Invoices_RecipientId",
                table: "RGB_Invoices",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_RGB_Invoices_Status",
                table: "RGB_Invoices",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RGB_Invoices_WalletId",
                table: "RGB_Invoices",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_RGB_Wallets_StoreId",
                table: "RGB_Wallets",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RGB_Assets");

            migrationBuilder.DropTable(
                name: "RGB_Invoices");

            migrationBuilder.DropTable(
                name: "RGB_Wallets");
        }
    }
}
