using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiRequestLogTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                schema: "mimamori",
                table: "AiRequestLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                schema: "mimamori",
                table: "AiRequestLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalTokens",
                schema: "mimamori",
                table: "AiRequestLogs",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                schema: "mimamori",
                table: "AiRequestLogs");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                schema: "mimamori",
                table: "AiRequestLogs");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                schema: "mimamori",
                table: "AiRequestLogs");
        }
    }
}
