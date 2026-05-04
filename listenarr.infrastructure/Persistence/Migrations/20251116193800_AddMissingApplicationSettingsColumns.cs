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
    public partial class AddMissingApplicationSettingsColumns_20251116193800 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migration note:
            // Historically some databases had these columns added manually which caused
            // later migrations that attempted to add them again to fail with
            // "duplicate column name" errors. SQLite does not support "ALTER TABLE IF NOT EXISTS"
            // for columns, and dynamically executing DDL based on PRAGMA results from inside
            // a migration is fragile across the EF migrations execution environment.

            // To be safe and predictable we intentionally make this migration a no-op at runtime.
            // The columns are tracked in the model snapshot and should already exist in databases
            // that were patched manually. For environments that are missing these columns, run
            // the helper SQL script at `scripts/add-missing-applicationsettings-columns.sql`
            // (it will add only missing columns and set sensible defaults). This keeps the
            // migration application deterministic while providing a straightforward manual
            // fix for affected databases.

            // No runtime SQL executed here on purpose.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty to avoid accidental data loss. If you need to remove these
            // columns, do so manually after ensuring data has been preserved.
        }
    }
}


