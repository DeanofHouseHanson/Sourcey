using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuyList.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AsinCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AsinCandidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Asin = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Brand = table.Column<string>(type: "TEXT", nullable: true),
                    Mpn = table.Column<string>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SalesRank = table.Column<int>(type: "INTEGER", nullable: true),
                    MonthlySales = table.Column<int>(type: "INTEGER", nullable: true),
                    Avg90BuyBox = table.Column<long>(type: "INTEGER", nullable: true),
                    FbaSellerCount = table.Column<int>(type: "INTEGER", nullable: true),
                    FbaSellerCountIsApprox = table.Column<bool>(type: "INTEGER", nullable: false),
                    AmazonOnListing = table.Column<bool>(type: "INTEGER", nullable: false),
                    WeightGrams = table.Column<int>(type: "INTEGER", nullable: true),
                    KeepaFbaFee = table.Column<long>(type: "INTEGER", nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsinCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AsinCandidates_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsinCandidates_ProductId_Asin",
                table: "AsinCandidates",
                columns: new[] { "ProductId", "Asin" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsinCandidates");
        }
    }
}
