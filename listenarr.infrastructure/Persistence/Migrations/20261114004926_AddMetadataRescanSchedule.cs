using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataRescanSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MetadataRescanBlockedCount",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataRescanDueUtc",
                table: "AudiobookFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "MetadataRescanFailureCount",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MetadataRescanLastOutcome",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataRescanUnavailableCount",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookFiles_MetadataRescanDue",
                table: "AudiobookFiles",
                column: "MetadataRescanDueUtc",
                filter: "\"DurationSeconds\" IS NULL OR \"Format\" IS NULL OR \"SampleRate\" IS NULL OR \"Size\" IS NULL OR \"Size\" = 64");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AudiobookFiles_MetadataRescanDue",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "MetadataRescanBlockedCount",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "MetadataRescanDueUtc",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "MetadataRescanFailureCount",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "MetadataRescanLastOutcome",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "MetadataRescanUnavailableCount",
                table: "AudiobookFiles");
        }
    }
}
