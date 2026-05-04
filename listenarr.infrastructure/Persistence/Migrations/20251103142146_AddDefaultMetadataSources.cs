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
    public partial class AddDefaultMetadataSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only insert if they don't already exist (prevents duplicate key errors on re-run)
            migrationBuilder.Sql(@"
                INSERT INTO ApiConfigurations (Id, Name, BaseUrl, ApiKey, Type, IsEnabled, Priority, HeadersJson, ParametersJson, CreatedAt)
                SELECT 
                    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
                    'Audimeta',
                    'https://audimeta.de',
                    '',
                    'metadata',
                    1,
                    1,
                    '{}',
                    '{}',
                    datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ApiConfigurations WHERE Name = 'Audimeta');
            ");

            migrationBuilder.Sql(@"
                INSERT INTO ApiConfigurations (Id, Name, BaseUrl, ApiKey, Type, IsEnabled, Priority, HeadersJson, ParametersJson, CreatedAt)
                SELECT 
                    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
                    'Audnexus',
                    'https://api.audnex.us',
                    '',
                    'metadata',
                    1,
                    2,
                    '{}',
                    '{}',
                    datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ApiConfigurations WHERE Name = 'Audnexus');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the default metadata sources
            migrationBuilder.Sql(@"
                DELETE FROM ApiConfigurations 
                WHERE Name IN ('Audimeta', 'Audnexus') 
                AND Type = 'metadata';
            ");
        }
    }
}


