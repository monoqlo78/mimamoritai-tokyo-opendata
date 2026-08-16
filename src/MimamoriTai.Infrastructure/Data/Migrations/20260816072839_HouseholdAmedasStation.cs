using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HouseholdAmedasStation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AmedasStationCode",
                schema: "mimamori",
                table: "Households",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmedasStationName",
                schema: "mimamori",
                table: "Households",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmedasStationCode",
                schema: "mimamori",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "AmedasStationName",
                schema: "mimamori",
                table: "Households");
        }
    }
}
