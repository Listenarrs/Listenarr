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
    [Migration("20251116223000_AddMissingApplicationSettingsColumns_Final")]
    public partial class AddMissingApplicationSettingsColumns_Final_20251116223000 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add any columns that are expected by the model but may be missing in existing DBs.
            migrationBuilder.AddColumn<string>(
                name: "EnabledNotificationTriggers",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "book-added|book-downloading|book-available|book-completed");

            migrationBuilder.AddColumn<bool>(
                name: "PreferUsDomain",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseUsProxy",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UsProxyHost",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsProxyPort",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "UsProxyUsername",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsProxyPassword",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookUrl",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebhookUrl",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UsProxyPassword",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UsProxyUsername",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UsProxyPort",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UsProxyHost",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UseUsProxy",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "PreferUsDomain",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "EnabledNotificationTriggers",
                table: "ApplicationSettings");
        }
    }
}


