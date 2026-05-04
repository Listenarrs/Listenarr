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
    public partial class AddMissingApplicationSettingsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration records that the following columns were manually added to the ApplicationSettings table:
            // - EnabledNotificationTriggers (TEXT, NOT NULL, DEFAULT 'book-added|book-downloading|book-available|book-completed')
            // - PreferUsDomain (INTEGER, NOT NULL, DEFAULT 1)
            // - UseUsProxy (INTEGER, NOT NULL, DEFAULT 0)
            // - UsProxyHost (TEXT, NULL)
            // - UsProxyPort (INTEGER, NOT NULL, DEFAULT 0)
            // - UsProxyUsername (TEXT, NULL)
            // - UsProxyPassword (TEXT, NULL)
            //
            // These columns already exist in the database from manual additions.
            // This migration serves to track these changes in the migration history.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Since these columns were manually added and are now in production use,
            // we don't provide a down migration to avoid data loss.
            // If rollback is needed, the columns should be manually removed if appropriate.
        }
    }
}


