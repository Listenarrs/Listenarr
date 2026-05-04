/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SearchCandidateCap",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "SearchFuzzyThreshold",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "SearchResultCap",
                table: "ApplicationSettings");

            migrationBuilder.CreateTable(
                name: "DownloadHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DownloadId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    EventDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AudiobookId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DownloadClient = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DownloadClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OutputPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    WasImported = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadHistories_AudiobookId",
                table: "DownloadHistories",
                column: "AudiobookId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadHistories_DownloadId",
                table: "DownloadHistories",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadHistories_DownloadId_EventType",
                table: "DownloadHistories",
                columns: new[] { "DownloadId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadHistories_EventDate",
                table: "DownloadHistories",
                column: "EventDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadHistories");

            migrationBuilder.AddColumn<int>(
                name: "SearchCandidateCap",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "SearchFuzzyThreshold",
                table: "ApplicationSettings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SearchResultCap",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
