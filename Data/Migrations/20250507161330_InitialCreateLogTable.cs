using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMProxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreateLogTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequestPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    RequestMethod = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    ClientRequestBody = table.Column<string>(type: "TEXT", nullable: true),
                    UpstreamBackendName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    UpstreamUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    UpstreamRequestBody = table.Column<string>(type: "TEXT", nullable: true),
                    UpstreamStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    UpstreamResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    ProxyResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    WasSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RequestedModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiLogEntries", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiLogEntries");
        }
    }
}
