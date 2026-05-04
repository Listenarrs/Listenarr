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
    public partial class AddSearchSettingsToApplicationSettings_20251119170500 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableAmazonSearch",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableAudibleSearch",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableOpenLibrarySearch",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "SearchCandidateCap",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "SearchResultCap",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<double>(
                name: "SearchFuzzyThreshold",
                table: "ApplicationSettings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.20000000000000001);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAmazonSearch",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "EnableAudibleSearch",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "EnableOpenLibrarySearch",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "SearchCandidateCap",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "SearchResultCap",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "SearchFuzzyThreshold",
                table: "ApplicationSettings");
        }
    }
}


