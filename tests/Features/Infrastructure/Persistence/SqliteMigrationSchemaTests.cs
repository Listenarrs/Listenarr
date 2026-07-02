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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    /// <summary>
    /// Migrations are hand-written in this repo, and the regular test suite runs
    /// on the EF InMemory provider — which never executes them. That combination
    /// let a migration ship without its [Migration] attribute + Designer
    /// (20251124102000_AddMoveJobSourcePath): EF discovery never saw it, so every
    /// SQLite install was missing MoveJobs.SourcePath while the model mapped it,
    /// and the first full-entity query failed at runtime ("no such column").
    /// These tests migrate a REAL SQLite database and verify the outcome so that
    /// class of drift fails CI instead of production.
    /// </summary>
    [Trait("Area", "Persistence")]
    [Trait("Name", "SqliteMigrationSchemaTests")]
    public class SqliteMigrationSchemaTests
    {
        private static (SqliteConnection Connection, ListenArrDbContext Context) CreateMigratedSqliteContext()
        {
            // Shared in-memory database lives as long as the connection is open.
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            var context = new ListenArrDbContext(options);
            context.Database.Migrate();
            return (connection, context);
        }

        [Fact]
        [Trait("Scenario", "EveryModelColumnExistsAfterMigrate")]
        public void EveryMappedColumn_ExistsInMigratedSqliteSchema()
        {
            var (connection, context) = CreateMigratedSqliteContext();
            using var _conn = connection;
            using var _ctx = context;

            var failures = new List<string>();

            foreach (var entityType in context.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName))
                {
                    continue; // not mapped to a table (owned/view/keyless)
                }

                var storeObject = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
                var columns = entityType.GetProperties()
                    .Select(p => p.GetColumnName(storeObject))
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Distinct()
                    .ToList();
                if (columns.Count == 0)
                {
                    continue;
                }

                // SELECT every mapped column with LIMIT 0: succeeds only when the
                // migrated schema actually contains each one.
                var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {columnList} FROM \"{tableName}\" LIMIT 0";
                try
                {
                    using var reader = command.ExecuteReader();
                }
                catch (SqliteException ex)
                {
                    failures.Add($"{tableName}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                "Model maps columns the migrated SQLite schema does not have — a migration is missing, "
                + "not discovered (missing [Migration] attribute / Designer), or out of sync:\n"
                + string.Join("\n", failures));
        }

        [Fact]
        [Trait("Scenario", "MoveJobsSourcePathRegression")]
        public void MoveJobs_SourcePathColumn_ExistsAfterMigrate()
        {
            var (connection, context) = CreateMigratedSqliteContext();
            using var _conn = connection;
            using var _ctx = context;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('MoveJobs')";
            var columns = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            Assert.Contains("SourcePath", columns);
        }
    }
}
