using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuyList.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlocklistEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlocklistEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Asin = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Ean = table.Column<string>(type: "TEXT", maxLength: 14, nullable: true),
                    Mpn = table.Column<string>(type: "TEXT", nullable: true),
                    Brand = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ManualFlags = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AmazonSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Avg90BuyBox = table.Column<long>(type: "INTEGER", nullable: true),
                    SalesRank = table.Column<int>(type: "INTEGER", nullable: true),
                    MonthlySales = table.Column<int>(type: "INTEGER", nullable: true),
                    FbaSellerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AmazonOnListing = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmazonSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmazonSnapshots_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceOffers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true),
                    RetailerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    BuyPriceExVat = table.Column<long>(type: "INTEGER", nullable: false),
                    BuyPriceIncVat = table.Column<long>(type: "INTEGER", nullable: false),
                    StockStatus = table.Column<string>(type: "TEXT", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawEan = table.Column<string>(type: "TEXT", nullable: true),
                    RawMpn = table.Column<string>(type: "TEXT", nullable: true),
                    RawBrand = table.Column<string>(type: "TEXT", nullable: true),
                    RawTitle = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceOffers_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Opportunities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceOfferId = table.Column<int>(type: "INTEGER", nullable: false),
                    NetProfit = table.Column<long>(type: "INTEGER", nullable: false),
                    RoiPercent = table.Column<long>(type: "INTEGER", nullable: false),
                    MarginPercent = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxBuyPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    EstMonthlyProfit = table.Column<long>(type: "INTEGER", nullable: false),
                    RiskLevel = table.Column<string>(type: "TEXT", nullable: false),
                    RiskAdjustedMonthlyProfit = table.Column<long>(type: "INTEGER", nullable: false),
                    DaysToSell = table.Column<long>(type: "INTEGER", nullable: true),
                    SuggestedQty = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Flags = table.Column<int>(type: "INTEGER", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Opportunities_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Opportunities_SourceOffers_SourceOfferId",
                        column: x => x.SourceOfferId,
                        principalTable: "SourceOffers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AmazonSnapshots_ProductId_CapturedAt",
                table: "AmazonSnapshots",
                columns: new[] { "ProductId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BlocklistEntries_Type_Value",
                table: "BlocklistEntries",
                columns: new[] { "Type", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_ProductId",
                table: "Opportunities",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_RiskAdjustedMonthlyProfit",
                table: "Opportunities",
                column: "RiskAdjustedMonthlyProfit");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_SourceOfferId",
                table: "Opportunities",
                column: "SourceOfferId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_Status",
                table: "Opportunities",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Asin",
                table: "Products",
                column: "Asin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Brand",
                table: "Products",
                column: "Brand");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Ean",
                table: "Products",
                column: "Ean");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Mpn",
                table: "Products",
                column: "Mpn");

            migrationBuilder.CreateIndex(
                name: "IX_SourceOffers_CheckedAt",
                table: "SourceOffers",
                column: "CheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SourceOffers_ProductId",
                table: "SourceOffers",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceOffers_RawEan",
                table: "SourceOffers",
                column: "RawEan");

            migrationBuilder.CreateIndex(
                name: "IX_SourceOffers_SourceUrl",
                table: "SourceOffers",
                column: "SourceUrl",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AmazonSnapshots");

            migrationBuilder.DropTable(
                name: "BlocklistEntries");

            migrationBuilder.DropTable(
                name: "Opportunities");

            migrationBuilder.DropTable(
                name: "SourceOffers");

            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
