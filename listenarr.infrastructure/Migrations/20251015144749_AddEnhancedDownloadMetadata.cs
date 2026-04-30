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

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnhancedDownloadMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Asin",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExpectedFileSize",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Isbn",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Publisher",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Runtime",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Series",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesNumber",
                table: "Downloads",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Asin",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "ExpectedFileSize",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Isbn",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Publisher",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Runtime",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Series",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "SeriesNumber",
                table: "Downloads");
        }
    }
}


