using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessExecutionLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    Arguments = table.Column<string>(type: "TEXT", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    TimedOut = table.Column<bool>(type: "INTEGER", nullable: false),
                    Stdout = table.Column<string>(type: "TEXT", nullable: true),
                    Stderr = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessExecutionLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessExecutionLogs");
        }
    }
}
