using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuyList.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KeepaEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AsinLookupStatus",
                table: "Products",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "KeepaCheckedAt",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FbaSellerCountIsApprox",
                table: "AmazonSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "KeepaFbaFee",
                table: "AmazonSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WeightGrams",
                table: "AmazonSnapshots",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AsinLookupStatus",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "KeepaCheckedAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "FbaSellerCountIsApprox",
                table: "AmazonSnapshots");

            migrationBuilder.DropColumn(
                name: "KeepaFbaFee",
                table: "AmazonSnapshots");

            migrationBuilder.DropColumn(
                name: "WeightGrams",
                table: "AmazonSnapshots");
        }
    }
}
