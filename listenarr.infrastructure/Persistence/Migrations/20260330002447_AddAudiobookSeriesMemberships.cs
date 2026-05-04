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
    public partial class AddAudiobookSeriesMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudiobookSeriesMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SeriesNumber = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SeriesAsin = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookSeriesMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookSeriesMemberships_Audiobooks_AudiobookId",
                        column: x => x.AudiobookId,
                        principalTable: "Audiobooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookSeriesMemberships_AudiobookId",
                table: "AudiobookSeriesMemberships",
                column: "AudiobookId");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookSeriesMemberships_AudiobookId_IsPrimary",
                table: "AudiobookSeriesMemberships",
                columns: new[] { "AudiobookId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookSeriesMemberships_AudiobookId_SortOrder",
                table: "AudiobookSeriesMemberships",
                columns: new[] { "AudiobookId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudiobookSeriesMemberships");
        }
    }
}
