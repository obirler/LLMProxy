using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMProxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEffectiveModelToLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectiveModelName",
                table: "ApiLogEntries",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveModelName",
                table: "ApiLogEntries");
        }
    }
}
