using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarkerlessMoveExecutionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExecutionProtocolVersion",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "SourceDirectoryCleanupState",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "SourceDirectoryObjectIdentity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDirectoryObjectIdentity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePhysicalObjectIdentity",
                table: "MoveJobEntries",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPhysicalObjectIdentity",
                table: "MoveJobEntries",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectIdentity",
                table: "MoveJobCreatedDirectories",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionProtocolVersion",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceDirectoryCleanupState",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceDirectoryObjectIdentity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetDirectoryObjectIdentity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourcePhysicalObjectIdentity",
                table: "MoveJobEntries");

            migrationBuilder.DropColumn(
                name: "TargetPhysicalObjectIdentity",
                table: "MoveJobEntries");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentity",
                table: "MoveJobCreatedDirectories");
        }
    }
}
