using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHeatReadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HeatReadings",
                schema: "mimamori",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PointCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AreaName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Wbgt = table.Column<double>(type: "float", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    TemperatureC = table.Column<double>(type: "float", nullable: true),
                    HumidityPercent = table.Column<double>(type: "float", nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedToStreamAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatReadings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HeatReadings_PointCode_ObservedAtUtc",
                schema: "mimamori",
                table: "HeatReadings",
                columns: new[] { "PointCode", "ObservedAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HeatReadings_PublishedToStreamAtUtc",
                schema: "mimamori",
                table: "HeatReadings",
                column: "PublishedToStreamAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HeatReadings",
                schema: "mimamori");
        }
    }
}
