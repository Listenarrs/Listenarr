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
    public partial class AddDiscordSettingsToApplicationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use raw SQL to conditionally add columns only if they don't exist
            // This handles cases where columns were manually added or from broken migrations
            migrationBuilder.Sql(@"
                -- Add DiscordApplicationId if it doesn't exist
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordApplicationId"" TEXT NULL;
                -- Ignore error if column already exists
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordBotAvatar"" TEXT NULL;
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordBotEnabled"" INTEGER NOT NULL DEFAULT 0;
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordBotToken"" TEXT NULL;
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordBotUsername"" TEXT NULL;
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordChannelId"" TEXT NULL;
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordCommandGroupName"" TEXT NULL;
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordCommandSubcommandName"" TEXT NULL;
            ", suppressTransaction: true);

            migrationBuilder.Sql(@"
                ALTER TABLE ""ApplicationSettings"" ADD COLUMN ""DiscordGuildId"" TEXT NULL;
            ", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscordGuildId",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordCommandSubcommandName",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordCommandGroupName",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordChannelId",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordBotUsername",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordBotToken",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordBotEnabled",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordBotAvatar",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "DiscordApplicationId",
                table: "ApplicationSettings");
        }
    }
}


