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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data.Common;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    /// <summary>
    /// Migrations must be scaffolded with dotnet ef, and many tests still run
    /// on the EF InMemory provider — which never executes them. Missing migration
    /// metadata once let a migration ship without its [Migration] attribute + Designer
    /// (20251124102000_AddMoveJobSourcePath): EF discovery never saw it, so every
    /// SQLite install was missing MoveJobs.SourcePath while the model mapped it,
    /// and the first full-entity query failed at runtime ("no such column").
    /// These tests migrate a REAL SQLite database and verify the outcome so that
    /// class of drift fails CI instead of production.
    /// </summary>
    [Trait("Area", "Persistence")]
    [Trait("Name", "SqliteMigrationSchemaTests")]
    [Trait("Category", "Infrastructure")]
    public class SqliteMigrationSchemaTests : BaseTests
    {
        private const string CanaryMigrationFrontierId =
            "20260621002226_AddApplicationSettingsConcurrency";
        private const string PhysicalIdentityMigrationId =
            "20260730033245_AddPhysicalFileIdentityAndMoveCleanupProtection";
        private const string PhysicalIdentityMigrationPredecessorId =
            "20260727000644_AddOwnershipRecoveryProtocols";
        private const string MarkerlessMoveMigrationId =
            "20260805192525_AddMarkerlessMoveExecutionState";
        private const string MarkerlessFileMutationMigrationId =
            "20260805202154_AddMarkerlessFileMutationJournal";

        public static TheoryData<string> ChangedMigrationIds => new()
        {
            "20251124102000_AddMoveJobSourcePath",
            "20260702200000_AddProcessExecutionLogs",
            "20260703024452_AddMoveJobDeleteEmptySource",
            "20260708223635_AddDurableFilesystemMoves",
            "20260708224312_AddMoveJobRelocationForeignKey",
            "20260708224705_AddMoveJobLeaseGeneration",
            "20260708224900_AddRootFolderRelocationSkippedItems",
            "20260708225028_MakeRootFolderRelocationRootNullable",
            "20260708225100_DropRootFolderRelocationRootForeignKey",
            "20260708225144_SetRootFolderRelocationRootDeleteBehavior",
            "20260710172532_AddMoveJobSourceCleanupBoundary",
            "20260713181804_HardenMoveExecutionAndScanHandoffs",
            "20260717143713_AddLibraryDirectoryOwnership",
            "20260726042801_AddDirectoryObjectIdentityAuthorization",
            "20260727000644_AddOwnershipRecoveryProtocols",
            PhysicalIdentityMigrationId,
            MarkerlessMoveMigrationId,
            MarkerlessFileMutationMigrationId
        };

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
        [Trait("Scenario", "PullRequestMigrationsHaveNoNonTransactionalOperationWarnings")]
        public async Task PullRequestMigrations_AfterCanaryFrontier_HaveNoNonTransactionalOperationWarnings()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var baselineOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using (var baseline = new ListenArrDbContext(baselineOptions))
            {
                await baseline.GetService<IMigrator>().MigrateAsync(
                    CanaryMigrationFrontierId);
            }

            var guardedOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .ConfigureWarnings(warnings => warnings.Throw(
                    RelationalEventId.NonTransactionalMigrationOperationWarning))
                .Options;
            await using var guarded = new ListenArrDbContext(guardedOptions);

            await guarded.Database.MigrateAsync();
        }

        [Fact]
        [Trait("Scenario", "MigrationHistoryMatchesModel")]
        public async Task MigrationHistory_HasNoPendingModelChanges()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            await context.Database.MigrateAsync();

            Assert.False(
                context.Database.HasPendingModelChanges(),
                "The configured EF model differs from the accumulated migration snapshots. "
                + "Regenerate migrations with dotnet ef migrations add instead of hand-authoring them.");
        }

        [Fact]
        [Trait("Scenario", "AudiobookFileOwnershipIdentityDefaultsAndIndexes")]
        public async Task AudiobookFileOwnershipMigration_PreservesRowsAndCreatesOwnershipIndexes()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260713181804_HardenMoveExecutionAndScanHandoffs");
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Audiobooks" ("Id", "Explicit", "Abridged", "Monitored")
                VALUES (1, 0, 0, 1)
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AudiobookFiles" ("AudiobookId", "Path", "CreatedAt")
                VALUES (1, '/library/book-one.m4b', CURRENT_TIMESTAMP),
                       (1, '/library/book-two.m4b', CURRENT_TIMESTAMP)
                """);

            await migrator.MigrateAsync("20260717143713_AddLibraryDirectoryOwnership");
            var repairResult = ListenarrDatabaseMigrationPreflight
                .RepairPostMigrationData(context);
            Assert.Equal(0, repairResult.MoveJobsRepaired);
            Assert.Equal(2, repairResult.AudiobookFilesRepaired);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "PathCaseSensitivity", "PathCaseSensitivityMode",
                           "PathIdentityVersion", "PathIdentityState"
                    FROM "AudiobookFiles"
                    ORDER BY "Id"
                    LIMIT 1
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Unknown", reader.GetString(0));
                Assert.Equal("Auto", reader.GetString(1));
                Assert.Equal(1, reader.GetInt32(2));
                Assert.Equal("Unavailable", reader.GetString(3));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM pragma_index_list('AudiobookFiles')
                    WHERE name IN (
                        'IX_AudiobookFiles_PathIdentityLookupKey',
                        'IX_AudiobookFiles_PathOwnershipKey')
                    """;
                Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
            }

            await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"AudiobookFiles\" SET \"PathOwnershipKey\" = 'owned:path' WHERE \"Id\" = 1");
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"AudiobookFiles\" SET \"PathOwnershipKey\" = 'owned:path' WHERE \"Id\" = 2"));
        }

        [Fact]
        [Trait("Scenario", "PhysicalIdentityAndCleanupProtectionMigration")]
        public async Task PhysicalIdentityMigration_PreservesLegacyRowsAcrossUpgradeAndDowngrade()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            var moveJobId = Guid.NewGuid();

            await using var context = new ListenArrDbContext(options);
            var migrations = context.Database.GetMigrations().ToList();
            Assert.Single(migrations, migration => migration == PhysicalIdentityMigrationId);
            Assert.DoesNotContain(
                "20260728190000_AddAudiobookFilePhysicalIdentity",
                migrations);
            Assert.DoesNotContain(
                "20260728193000_AddMoveCleanupProtectionVersion",
                migrations);

            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PhysicalIdentityMigrationPredecessorId);
            Assert.False(await ColumnExistsAsync(
                connection,
                "AudiobookFiles",
                "PhysicalObjectIdentity"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "MoveJobEntries",
                "CleanupProtectionVersion"));

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Audiobooks" ("Id", "Explicit", "Abridged", "Monitored")
                VALUES (101, 0, 0, 1)
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AudiobookFiles" ("Id", "AudiobookId", "Path", "CreatedAt",
                    "PathCaseSensitivity", "PathCaseSensitivityMode", "PathIdentityVersion",
                    "PathIdentityState")
                VALUES (201, 101, '/library/legacy.m4b', CURRENT_TIMESTAMP,
                    'Unknown', 'Auto', 1, 'Unavailable')
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "MoveJobs" ("Id", "AudiobookId", "EnqueuedAt", "Status",
                    "AttemptCount", "DeleteEmptySource", "FailureKind", "IdentityKeyVersion",
                    "LeaseGeneration", "Phase")
                VALUES ({0}, 101, CURRENT_TIMESTAMP, 'Queued', 0, 0, 'None', 5, 0, 'None')
                """,
                moveJobId);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "MoveJobEntries" ("Id", "MoveJobId", "RelativePath", "EntryType",
                    "Length", "LastWriteTimeUtc", "CopyState", "CleanupState")
                VALUES (301, {0}, 'legacy.m4b', 'File', 1234, CURRENT_TIMESTAMP,
                    'Pending', 'Pending')
                """,
                moveJobId);

            await migrator.MigrateAsync(PhysicalIdentityMigrationId);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "PhysicalIdentityVersion", "PhysicalObjectIdentity",
                           "PhysicalIdentityObservedAtUtc"
                    FROM "AudiobookFiles"
                    WHERE "Id" = 201
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.IsDBNull(2));
            }

            Assert.Equal(
                0L,
                (long)(await ExecuteScalarAsync(
                    connection,
                    "SELECT \"CleanupProtectionVersion\" FROM \"MoveJobEntries\" WHERE \"Id\" = 301"))!);
            Assert.True(await IndexExistsAsync(
                connection,
                "MoveJobEntries",
                "IX_MoveJobEntries_MoveJobId_RelativePath"));

            await migrator.MigrateAsync(PhysicalIdentityMigrationPredecessorId);

            Assert.False(await ColumnExistsAsync(
                connection,
                "AudiobookFiles",
                "PhysicalIdentityObservedAtUtc"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "AudiobookFiles",
                "PhysicalIdentityVersion"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "AudiobookFiles",
                "PhysicalObjectIdentity"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "MoveJobEntries",
                "CleanupProtectionVersion"));
            Assert.Equal(
                "/library/legacy.m4b",
                (string)(await ExecuteScalarAsync(
                    connection,
                    "SELECT \"Path\" FROM \"AudiobookFiles\" WHERE \"Id\" = 201"))!);
            Assert.Equal(
                "legacy.m4b",
                (string)(await ExecuteScalarAsync(
                    connection,
                    "SELECT \"RelativePath\" FROM \"MoveJobEntries\" WHERE \"Id\" = 301"))!);
            Assert.True(await IndexExistsAsync(
                connection,
                "MoveJobEntries",
                "IX_MoveJobEntries_MoveJobId_RelativePath"));
            Assert.True(await IndexExistsAsync(
                connection,
                "AudiobookFiles",
                "IX_AudiobookFiles_PathIdentityLookupKey"));
            Assert.True(await IndexExistsAsync(
                connection,
                "AudiobookFiles",
                "IX_AudiobookFiles_PathOwnershipKey"));

            await using var foreignKeyCheck = connection.CreateCommand();
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            await using var foreignKeyReader = await foreignKeyCheck.ExecuteReaderAsync();
            Assert.False(await foreignKeyReader.ReadAsync());
        }

        [Fact]
        [Trait("Scenario", "LibraryDirectoryOwnershipIndexes")]
        public async Task LibraryDirectoryOwnershipMigration_CreatesDurableOwnershipIndexes()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            await context.Database.MigrateAsync();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM pragma_index_list('LibraryDirectoryOwnerships')
                    WHERE name IN (
                        'IX_LibraryDirectoryOwnerships_CreationOperationId_State',
                        'IX_LibraryDirectoryOwnerships_OwnershipToken',
                        'IX_LibraryDirectoryOwnerships_PathIdentityLookupKey',
                        'IX_LibraryDirectoryOwnerships_PathOwnershipKey')
                    """;
                Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
            }

            const string insertSql =
                """
                INSERT INTO "LibraryDirectoryOwnerships" (
                    "Path", "CanonicalPath", "PathSyntax", "PathCaseSensitivity",
                    "PathCaseSensitivityMode", "PathIdentityBoundary",
                    "PathIdentityLookupKey", "PathOwnershipKey", "OwnershipToken",
                    "State", "CreationWorkflow", "CreatedAt", "UpdatedAt")
                VALUES ({0}, {0}, 'Unix', 'Sensitive', 'Sensitive', '/library',
                    {1}, {2}, {3}, 'Owned', 'migration-test', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                """;
            await context.Database.ExecuteSqlRawAsync(
                insertSql,
                "/library/author-one",
                "lookup:one",
                "ownership:one",
                "11111111111111111111111111111111");
            await context.Database.ExecuteSqlRawAsync(
                insertSql,
                "/library/author-two",
                "lookup:two",
                null,
                "22222222222222222222222222222222");

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"LibraryDirectoryOwnerships\" SET \"PathOwnershipKey\" = 'ownership:one' WHERE \"OwnershipToken\" = '22222222222222222222222222222222'"));
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    insertSql,
                    "/library/author-three",
                    "lookup:three",
                    null,
                    "11111111111111111111111111111111"));
        }

        [Fact]
        [Trait("Scenario", "SingleDefaultRootFolderInvariant")]
        public async Task SingleDefaultRootFolderMigration_ReconcilesDuplicatesAndEnforcesUniqueness()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260717143713_AddLibraryDirectoryOwnership");
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "RootFolders" ("Id", "Name", "Path", "IsDefault")
                VALUES (10, 'First', '/library/first', 1),
                       (20, 'Second', '/library/second', 1),
                       (30, 'Third', '/library/third', 0)
                """);

            var repaired = ListenarrDatabaseMigrationPreflight.RepairLegacyData(context);
            Assert.Equal(1, repaired.DefaultRootsNormalized);
            await migrator.MigrateAsync();

            var defaults = await context.RootFolders
                .AsNoTracking()
                .Where(root => root.IsDefault)
                .Select(root => root.Id)
                .ToListAsync();
            Assert.Equal([10], defaults);
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"RootFolders\" SET \"IsDefault\" = 1 WHERE \"Id\" = 20"));
        }

        [Fact]
        [Trait("Scenario", "OwnershipRootForeignKeyUpgrade")]
        public async Task OwnershipRootForeignKeyMigration_AddsSetNullRelationship()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(
                "20260726042801_AddDirectoryObjectIdentityAuthorization");
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "RootFolders" ("Id", "Name", "Path", "IsDefault")
                VALUES (1, 'Library', '/library', 0);

                INSERT INTO "LibraryDirectoryOwnerships" (
                    "Id", "Path", "CanonicalPath", "PathSyntax",
                    "PathCaseSensitivity", "PathCaseSensitivityMode",
                    "PathIdentityBoundary", "PathIdentityLookupKey",
                    "PathOwnershipKey", "OwnershipToken", "State",
                    "CreationWorkflow", "CreatedAt", "UpdatedAt",
                    "ManagedRootFolderId")
                VALUES (
                    10, '/library/book', '/library/book', 'Unix',
                    'Sensitive', 'Sensitive', '/library/book', 'lookup-10',
                    'ownership-10', '10101010101010101010101010101010',
                    'Owned', 'test', '2026-07-27T00:00:00Z',
                    '2026-07-27T00:00:00Z', 1);
                """);

            await migrator.MigrateAsync(
                "20260805034058_AddLibraryDirectoryOwnershipRootForeignKey");

            await using (var foreignKeyCommand = connection.CreateCommand())
            {
                foreignKeyCommand.CommandText =
                    """
                    SELECT "on_delete"
                    FROM pragma_foreign_key_list('LibraryDirectoryOwnerships')
                    WHERE "table" = 'RootFolders'
                      AND "from" = 'ManagedRootFolderId'
                    """;
                Assert.Equal(
                    "SET NULL",
                    (await foreignKeyCommand.ExecuteScalarAsync())?.ToString());
            }

            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"RootFolders\" WHERE \"Id\" = 1");
            await using var ownershipCommand = connection.CreateCommand();
            ownershipCommand.CommandText =
                "SELECT \"ManagedRootFolderId\" FROM \"LibraryDirectoryOwnerships\" WHERE \"Id\" = 10";
            Assert.Equal(DBNull.Value, await ownershipCommand.ExecuteScalarAsync());
        }

        [Fact]
        [Trait("Scenario", "MarkerlessMoveExecutionStateUpgrade")]
        public async Task MarkerlessMoveExecutionStateMigration_PreservesLegacyRowsAndDefaults()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(
                "20260805034058_AddLibraryDirectoryOwnershipRootForeignKey");

            var moveJobId = Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "MoveJobs" (
                    "Id", "AudiobookId", "EnqueuedAt", "Status",
                    "AttemptCount", "DeleteEmptySource", "FailureKind",
                    "IdentityKeyVersion", "LeaseGeneration", "Phase")
                VALUES ({0}, 501, CURRENT_TIMESTAMP, 'Queued', 0, 1,
                    'None', 5, 0, 'None')
                """,
                moveJobId);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "MoveJobEntries" (
                    "MoveJobId", "RelativePath", "EntryType", "Length",
                    "LastWriteTimeUtc", "CopyState", "CleanupState",
                    "CleanupProtectionVersion")
                VALUES ({0}, 'book.m4b', 'File', 1234, CURRENT_TIMESTAMP,
                    'Pending', 'Pending', 0)
                """,
                moveJobId);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "MoveJobCreatedDirectories" (
                    "MoveJobId", "Path", "State")
                VALUES ({0}, '/library/author/book', 'Planned')
                """,
                moveJobId);

            await migrator.MigrateAsync(MarkerlessMoveMigrationId);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "ExecutionProtocolVersion",
                           "SourceDirectoryCleanupState",
                           "SourceDirectoryObjectIdentity",
                           "TargetDirectoryObjectIdentity"
                    FROM "MoveJobs"
                    WHERE "Id" = $jobId
                    """;
                command.Parameters.AddWithValue("$jobId", moveJobId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(MoveExecutionProtocol.LegacyFilesystemArtifacts, reader.GetInt32(0));
                Assert.Equal("Pending", reader.GetString(1));
                Assert.True(reader.IsDBNull(2));
                Assert.True(reader.IsDBNull(3));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "SourcePhysicalObjectIdentity",
                           "TargetPhysicalObjectIdentity"
                    FROM "MoveJobEntries"
                    WHERE "MoveJobId" = $jobId
                    """;
                command.Parameters.AddWithValue("$jobId", moveJobId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.True(reader.IsDBNull(1));
            }

            Assert.Equal(
                DBNull.Value,
                await ExecuteScalarAsync(
                    connection,
                    "SELECT \"DirectoryObjectIdentity\" FROM \"MoveJobCreatedDirectories\""));

            await migrator.MigrateAsync(
                "20260805034058_AddLibraryDirectoryOwnershipRootForeignKey");
            Assert.False(await ColumnExistsAsync(
                connection,
                "MoveJobs",
                "ExecutionProtocolVersion"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "MoveJobEntries",
                "TargetPhysicalObjectIdentity"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "MoveJobCreatedDirectories",
                "DirectoryObjectIdentity"));
        }

        [Fact]
        [Trait("Scenario", "MarkerlessFileMutationJournalUpgrade")]
        public async Task MarkerlessFileMutationJournalMigration_CreatesDurableDefaultsAndIndexes()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(MarkerlessMoveMigrationId);
            Assert.False(await TableExistsAsync(
                connection,
                "FileMutationJournals"));

            await migrator.MigrateAsync(MarkerlessFileMutationMigrationId);
            Assert.True(await TableExistsAsync(
                connection,
                "FileMutationJournals"));
            var operationId = Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "FileMutationJournals" (
                    "OperationId", "Action", "SourcePath", "DestinationPath",
                    "SourcePhysicalObjectIdentity", "SourceLength", "State",
                    "CreatedAt", "UpdatedAt")
                VALUES ({0}, 'Move', '/source/book.m4b',
                    '/library/book.m4b', 'source-generation', 123,
                    'Planned', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                """,
                operationId);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "ProtocolVersion", "State",
                           "TargetPhysicalObjectIdentity", "AudiobookId"
                    FROM "FileMutationJournals"
                    WHERE "OperationId" = $operationId
                    """;
                command.Parameters.AddWithValue("$operationId", operationId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(
                    FileMutationProtocol.MarkerlessDatabaseState,
                    reader.GetInt32(0));
                Assert.Equal("Planned", reader.GetString(1));
                Assert.True(reader.IsDBNull(2));
                Assert.True(reader.IsDBNull(3));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT group_concat("name", ',')
                    FROM (
                        SELECT "name"
                        FROM pragma_index_list('FileMutationJournals')
                        WHERE "name" LIKE 'IX_FileMutationJournals_%'
                        ORDER BY "name")
                    """;
                Assert.Equal(
                    "IX_FileMutationJournals_State,"
                    + "IX_FileMutationJournals_UpdatedAt",
                    (await command.ExecuteScalarAsync())?.ToString());
            }

            await migrator.MigrateAsync(MarkerlessMoveMigrationId);
            Assert.False(await TableExistsAsync(
                connection,
                "FileMutationJournals"));
        }

        [Fact]
        [Trait("Scenario", "IntermediatePrDatabaseOrphanRepair")]
        public async Task MigrationPreflight_RepairsOrphanOwnershipReferencesBeforeForeignKey()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using var context = new ListenArrDbContext(options);
            await context.GetService<IMigrator>().MigrateAsync(
                LibraryDirectoryOwnershipMigrationPreflight.PredecessorMigrationId);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "LibraryDirectoryOwnerships" (
                    "Id", "Path", "CanonicalPath", "PathSyntax",
                    "PathCaseSensitivity", "PathCaseSensitivityMode",
                    "PathIdentityBoundary", "PathIdentityLookupKey",
                    "PathOwnershipKey", "OwnershipToken", "State",
                    "CreationWorkflow", "CreatedAt", "UpdatedAt",
                    "ManagedRootFolderId", "StateReason")
                VALUES
                    (101, '/removed', '/removed', 'Unix', 'Sensitive',
                     'Sensitive', '/removed', 'lookup-101', NULL,
                     '10110110110110110110110110110110', 'Removed', 'test',
                     '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z', 999,
                     'Legacy diagnostic' || char(10) || 'second line'),
                    (202, '/owned', '/owned', 'Unix', 'Sensitive',
                     'Sensitive', '/owned', 'lookup-202', 'ownership-202',
                     '20220220220220220220220220220220', 'Owned', 'test',
                     '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z', 999,
                     NULL);
                """);

            Assert.Equal(
                2,
                LibraryDirectoryOwnershipMigrationPreflight
                    .RepairLegacyForeignKeyReferences(context));
            Assert.Equal(
                0,
                LibraryDirectoryOwnershipMigrationPreflight
                    .RepairLegacyForeignKeyReferences(context));

            await using (var verifyCommand = connection.CreateCommand())
            {
                verifyCommand.CommandText =
                    """
                    SELECT group_concat(
                        "Id" || ':' || "State" || ':'
                        || coalesce("ManagedRootFolderId", '') || ':'
                        || coalesce("PathOwnershipKey", '') || ':'
                        || coalesce("StateReason", ''), ',')
                    FROM (
                        SELECT "Id", "State", "ManagedRootFolderId",
                               "PathOwnershipKey", "StateReason"
                        FROM "LibraryDirectoryOwnerships"
                        ORDER BY "Id")
                    """;
                Assert.Equal(
                    "101:Removed:::migration:original-managed-root:999\n"
                    + "Legacy diagnostic\nsecond line,"
                    + "202:Unavailable:::The persisted managed root no longer exists.",
                    (await verifyCommand.ExecuteScalarAsync())?.ToString());
            }

            var guardedOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .ConfigureWarnings(warnings => warnings.Throw(
                    RelationalEventId.NonTransactionalMigrationOperationWarning))
                .Options;
            await using var guarded = new ListenArrDbContext(guardedOptions);
            await guarded.Database.MigrateAsync();

            await using var foreignKeyCheckCommand = connection.CreateCommand();
            foreignKeyCheckCommand.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeyCheckCommand.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }

        [Fact]
        [Trait("Scenario", "IntermediatePrDatabaseCompatibility")]
        public async Task IntermediatePrDatabase_RetiredForeignKeyHistory_IsTolerated()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var baselineOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using (var baseline = new ListenArrDbContext(baselineOptions))
            {
                await baseline.Database.MigrateAsync();
                await baseline.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" =
                        '20260805034058_AddLibraryDirectoryOwnershipRootForeignKey';

                    INSERT OR IGNORE INTO "__EFMigrationsHistory" (
                        "MigrationId", "ProductVersion")
                    VALUES (
                        '20260726500000_AddLibraryDirectoryOwnershipRootForeignKey',
                        '10.0.8');
                    """);
            }

            var guardedOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .ConfigureWarnings(warnings => warnings.Throw(
                    RelationalEventId.NonTransactionalMigrationOperationWarning))
                .Options;
            await using (var guarded = new ListenArrDbContext(guardedOptions))
            {
                await guarded.Database.MigrateAsync();
            }

            await using var historyCommand = connection.CreateCommand();
            historyCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" IN (
                    '20260726500000_AddLibraryDirectoryOwnershipRootForeignKey',
                    '20260727000644_AddOwnershipRecoveryProtocols',
                    '20260805034058_AddLibraryDirectoryOwnershipRootForeignKey')
                """;
            Assert.Equal(3L, (long)(await historyCommand.ExecuteScalarAsync())!);

            await using var integrityCommand = connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", (await integrityCommand.ExecuteScalarAsync())?.ToString());

            await using var foreignKeyCheckCommand = connection.CreateCommand();
            foreignKeyCheckCommand.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeyCheckCommand.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }

        [Fact]
        [Trait("Scenario", "OwnershipRecoveryProtocolRetry")]
        public async Task OwnershipRecoveryMigration_InterruptedSchemaTransaction_RetriesCleanly()
        {
            await using var connection =
                new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var interruption = new InterruptOwnershipRecoveryMigration();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .AddInterceptors(interruption)
                .Options;
            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(
                "20260726042801_AddDirectoryObjectIdentityAuthorization");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                migrator.MigrateAsync(
                    "20260727000644_AddOwnershipRecoveryProtocols"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "RootFolderRelocations",
                "TargetIdentityEnrollmentState"));
            Assert.False(await TableExistsAsync(
                connection,
                "LibraryDirectoryOwnershipRetiredMarkers"));

            interruption.Enabled = false;
            await migrator.MigrateAsync(
                "20260727000644_AddOwnershipRecoveryProtocols");

            Assert.True(await ColumnExistsAsync(
                connection,
                "RootFolderRelocations",
                "TargetIdentityEnrollmentState"));
            Assert.True(await TableExistsAsync(
                connection,
                "LibraryDirectoryOwnershipRetiredMarkers"));
        }

        [Fact]
        [Trait("Scenario", "OwnershipRecoveryProtocolDowngrade")]
        public async Task OwnershipRecoveryMigration_DowngradeRevertsRecoverySchema()
        {
            await using var connection =
                new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(
                "20260727000644_AddOwnershipRecoveryProtocols");

            await migrator.MigrateAsync(
                "20260726042801_AddDirectoryObjectIdentityAuthorization");

            Assert.False(await ColumnExistsAsync(
                connection,
                "RootFolderRelocations",
                "TargetIdentityEnrollmentState"));
            Assert.False(await TableExistsAsync(
                connection,
                "LibraryDirectoryOwnershipRetiredMarkers"));
            await using var foreignKeyCommand = connection.CreateCommand();
            foreignKeyCommand.CommandText =
                """
                SELECT "on_delete"
                FROM pragma_foreign_key_list('LibraryDirectoryOwnerships')
                WHERE "table" = 'RootFolders'
                  AND "from" = 'ManagedRootFolderId'
                """;
            Assert.Null(await foreignKeyCommand.ExecuteScalarAsync());

            await migrator.MigrateAsync(
                "20260727000644_AddOwnershipRecoveryProtocols");
            Assert.True(await TableExistsAsync(
                connection,
                "LibraryDirectoryOwnershipRetiredMarkers"));
        }

        [Fact]
        [Trait("Scenario", "OwnershipRootForeignKeyRetry")]
        public async Task OwnershipRootForeignKeyMigration_DowngradeAndReapply_IsolatedCleanly()
        {
            await using var connection =
                new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync(
                "20260805034058_AddLibraryDirectoryOwnershipRootForeignKey");
            Assert.Equal(
                "SET NULL",
                await GetOwnershipRootForeignKeyDeleteBehaviorAsync(connection));

            await migrator.MigrateAsync(PhysicalIdentityMigrationId);
            Assert.Null(await GetOwnershipRootForeignKeyDeleteBehaviorAsync(connection));

            await migrator.MigrateAsync(
                "20260805034058_AddLibraryDirectoryOwnershipRootForeignKey");
            Assert.Equal(
                "SET NULL",
                await GetOwnershipRootForeignKeyDeleteBehaviorAsync(connection));
        }

        [Fact]
        [Trait("Scenario", "ConcurrentDefaultRootPromotions")]
        public async Task ConcurrentDefaultRootPromotions_CannotCommitTwoDefaults()
        {
            var databasePath = Path.Join(
                FileService.GetTempPath(),
                $"single-default-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(
                    $"Data Source={databasePath};Default Timeout=5",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using (var setup = new ListenArrDbContext(options))
            {
                await setup.Database.MigrateAsync();
                setup.RootFolders.AddRange(
                    new RootFolder { Id = 101, Name = "First", Path = "/library/first" },
                    new RootFolder { Id = 202, Name = "Second", Path = "/library/second" });
                await setup.SaveChangesAsync();
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> PromoteAsync(int rootId)
            {
                await using var context = new ListenArrDbContext(options);
                var root = await context.RootFolders.SingleAsync(candidate => candidate.Id == rootId);
                root.IsDefault = true;
                await start.Task;
                try
                {
                    await context.SaveChangesAsync();
                    return true;
                }
                catch (Exception exception) when (exception is
                    PersistenceException or DbUpdateException or SqliteException)
                {
                    return false;
                }
            }

            var firstPromotion = PromoteAsync(101);
            var secondPromotion = PromoteAsync(202);
            start.SetResult();
            var outcomes = await Task.WhenAll(firstPromotion, secondPromotion);

            Assert.Single(outcomes, committed => committed);
            await using var verification = new ListenArrDbContext(options);
            Assert.Single(await verification.RootFolders
                .AsNoTracking()
                .Where(root => root.IsDefault)
                .ToListAsync());
        }

        [Theory]
        [MemberData(nameof(ChangedMigrationIds))]
        [Trait("Scenario", "ChangedMigrationsDowngradeAndReapply")]
        public async Task ChangedMigration_CanDowngradeOneStepAndReapply(string migrationId)
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            var migrations = context.Database.GetMigrations().ToList();
            var migrationIndex = migrations.IndexOf(migrationId);
            Assert.True(
                migrationIndex > 0,
                $"Migration '{migrationId}' was not discovered or has no predecessor.");

            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrationId);
            Assert.Contains(migrationId, await context.Database.GetAppliedMigrationsAsync());

            await migrator.MigrateAsync(migrations[migrationIndex - 1]);
            Assert.DoesNotContain(migrationId, await context.Database.GetAppliedMigrationsAsync());

            await migrator.MigrateAsync(migrationId);
            Assert.Contains(migrationId, await context.Database.GetAppliedMigrationsAsync());
        }

        [Fact]
        [Trait("Scenario", "ExistingRowsReceiveValidEnumDefaults")]
        public async Task ExistingRows_MaterializeAfterDurableMoveMigrationAddsEnumColumns()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            var moveJobId = Guid.NewGuid();
            var enqueuedAt = DateTime.UtcNow;

            await using (var seedingContext = new ListenArrDbContext(options))
            {
                var migrator = seedingContext.GetService<IMigrator>();
                await migrator.MigrateAsync("20260703024452_AddMoveJobDeleteEmptySource");
                await seedingContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "RootFolders" ("Name", "Path", "IsDefault")
                    VALUES ({0}, {1}, {2})
                    """,
                    "Library",
                    "/library",
                    true);
                await seedingContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "MoveJobs" (
                        "Id", "AudiobookId", "RequestedPath", "EnqueuedAt", "Status",
                        "AttemptCount", "DeleteEmptySource", "SourcePath")
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
                    """,
                    moveJobId,
                    42,
                    "/library/New Title",
                    enqueuedAt,
                    nameof(MoveJobStatus.Queued),
                    0,
                    true,
                    "/library/Old Title");

                await migrator.MigrateAsync();
            }

            await using var verification = new ListenArrDbContext(options);
            var root = await verification.RootFolders.SingleAsync();
            var moveJob = await verification.MoveJobs.SingleAsync();

            Assert.Equal(FileSystemCaseSensitivityMode.Auto, root.CaseSensitivityMode);
            Assert.Equal(PathIdentityState.Unavailable, root.PathIdentityState);
            Assert.Equal(FileSystemCaseSensitivity.Unknown, root.ResolvedCaseSensitivity);
            Assert.Equal(MoveFailureKind.None, moveJob.FailureKind);
            Assert.Equal(MoveJobPhase.None, moveJob.Phase);
        }

        [Fact]
        [Trait("Scenario", "RootRelocationForeignKeySplitRestart")]
        public async Task RootRelocationForeignKeySplit_RestartPreservesRowsAndFinalSetNullContract()
        {
            var databasePath = Path.Join(
                FileService.GetTempPath(),
                $"relocation-fk-split-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(
                    $"Data Source={databasePath}",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            var relocationId = Guid.NewGuid();
            var moveJobId = Guid.NewGuid();
            var skippedItemId = Guid.NewGuid();

            try
            {
                await using (var beforeRestart = new ListenArrDbContext(options))
                {
                    var migrator = beforeRestart.GetService<IMigrator>();
                    await migrator.MigrateAsync(
                        "20260708225028_MakeRootFolderRelocationRootNullable");
                    await beforeRestart.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO "RootFolders" ("Id", "Name", "Path", "IsDefault", "CreatedAt")
                        VALUES ({101}, {"Library"}, {"/library"}, {true}, {DateTime.UtcNow});
                        """);
                    await beforeRestart.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO "RootFolderRelocations" (
                            "Id", "RootFolderId", "SourcePath", "TargetPath", "Mode", "Status",
                            "DesiredName", "DesiredIsDefault", "CreatedAt", "CompletedJobs",
                            "DeleteEmptySource", "SourceCaseSensitivityMode",
                            "TargetCaseSensitivityMode", "TotalJobs")
                        VALUES (
                            {relocationId}, {101}, {"/library"}, {"/library-new"},
                            {nameof(RootFolderRelocationMode.Relocate)},
                            {nameof(RootFolderRelocationStatus.Running)},
                            {"Library"}, {true}, {DateTime.UtcNow}, {0}, {false},
                            {nameof(FileSystemCaseSensitivityMode.Auto)},
                            {nameof(FileSystemCaseSensitivityMode.Auto)}, {1});
                        """);
                    await beforeRestart.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO "MoveJobs" (
                            "Id", "AudiobookId", "EnqueuedAt", "Status", "AttemptCount",
                            "DeleteEmptySource", "FailureKind", "IdentityKeyVersion", "Phase",
                            "RelocationId")
                        VALUES (
                            {moveJobId}, {501}, {DateTime.UtcNow},
                            {nameof(MoveJobStatus.Queued)}, {0}, {false},
                            {nameof(MoveFailureKind.None)}, {0}, {nameof(MoveJobPhase.None)},
                            {relocationId});
                        """);
                    await beforeRestart.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO "RootFolderRelocationSkippedItems" (
                            "Id", "RelocationId", "AudiobookId", "Reason", "CreatedAt")
                        VALUES (
                            {skippedItemId}, {relocationId}, {502}, {"Skipped for test"},
                            {DateTimeOffset.UtcNow});
                        """);

                    await migrator.MigrateAsync(
                        "20260708225100_DropRootFolderRelocationRootForeignKey");
                    await beforeRestart.Database.OpenConnectionAsync();
                    var beforeRestartConnection =
                        (SqliteConnection)beforeRestart.Database.GetDbConnection();

                    Assert.Equal(
                        0L,
                        (long)(await ExecuteScalarAsync(
                            beforeRestartConnection,
                            """
                            SELECT COUNT(*)
                            FROM pragma_foreign_key_list('RootFolderRelocations')
                            WHERE "table" = 'RootFolders'
                              AND "from" = 'RootFolderId';
                            """))!);
                    Assert.Equal(
                        1L,
                        (long)(await ExecuteScalarAsync(
                            beforeRestartConnection,
                            "SELECT COUNT(*) FROM \"RootFolderRelocations\";"))!);
                }

                await using var afterRestart = new ListenArrDbContext(options);
                var resumedMigrator = afterRestart.GetService<IMigrator>();
                await resumedMigrator.MigrateAsync(
                    "20260708225144_SetRootFolderRelocationRootDeleteBehavior");
                await afterRestart.Database.OpenConnectionAsync();
                var afterRestartConnection =
                    (SqliteConnection)afterRestart.Database.GetDbConnection();

                Assert.Equal(
                    "SET NULL",
                    (await ExecuteScalarAsync(
                        afterRestartConnection,
                        """
                        SELECT "on_delete"
                        FROM pragma_foreign_key_list('RootFolderRelocations')
                        WHERE "table" = 'RootFolders'
                          AND "from" = 'RootFolderId';
                        """))?.ToString());
                Assert.Equal(
                    relocationId,
                    Guid.Parse((await ExecuteScalarAsync(
                        afterRestartConnection,
                        "SELECT \"Id\" FROM \"RootFolderRelocations\" LIMIT 1;"))!.ToString()!));
                Assert.Equal(
                    moveJobId,
                    Guid.Parse((await ExecuteScalarAsync(
                        afterRestartConnection,
                        "SELECT \"Id\" FROM \"MoveJobs\" WHERE \"RelocationId\" IS NOT NULL LIMIT 1;"))!.ToString()!));
                Assert.Equal(
                    skippedItemId,
                    Guid.Parse((await ExecuteScalarAsync(
                        afterRestartConnection,
                        "SELECT \"Id\" FROM \"RootFolderRelocationSkippedItems\" LIMIT 1;"))!.ToString()!));

                await afterRestart.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"RootFolders\" WHERE \"Id\" = 101;");
                Assert.Equal(
                    DBNull.Value,
                    await ExecuteScalarAsync(
                        afterRestartConnection,
                        "SELECT \"RootFolderId\" FROM \"RootFolderRelocations\" LIMIT 1;"));
                Assert.Equal(
                    1L,
                    (long)(await ExecuteScalarAsync(
                        afterRestartConnection,
                        "SELECT COUNT(*) FROM \"MoveJobs\" WHERE \"Id\" IS NOT NULL;"))!);
                Assert.Equal(
                    1L,
                    (long)(await ExecuteScalarAsync(
                        afterRestartConnection,
                        "SELECT COUNT(*) FROM \"RootFolderRelocationSkippedItems\";"))!);
                Assert.Equal(
                    0L,
                    (long)(await ExecuteScalarAsync(
                        afterRestartConnection,
                        "SELECT COUNT(*) FROM pragma_foreign_key_check;"))!);
                Assert.Equal(
                    "ok",
                    (await ExecuteScalarAsync(
                        afterRestartConnection,
                        "PRAGMA integrity_check;"))?.ToString());
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                File.Delete(databasePath);
            }
        }

        [Fact]
        [Trait("Scenario", "NullableRelocationRootDowngradeFailsClosed")]
        public async Task NullableRelocationRoot_DowngradeRejectsOrphanHistoryWithoutCorruption()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            var relocationId = Guid.NewGuid();

            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260708225144_SetRootFolderRelocationRootDeleteBehavior");

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "RootFolders" ("Name", "Path", "IsDefault", "CreatedAt")
                VALUES ({"Deleted Library"}, {"/library"}, {true}, {DateTime.UtcNow});
                """);
            var rootId = (long)(await ExecuteScalarAsync(
                connection,
                "SELECT last_insert_rowid();"))!;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "RootFolderRelocations" (
                    "Id", "RootFolderId", "SourcePath", "TargetPath", "Mode", "Status",
                    "DesiredName", "DesiredIsDefault", "CompletedAt", "CreatedAt",
                    "UpdatedAt", "CompletedJobs", "DeleteEmptySource",
                    "SourceCaseSensitivityMode", "TargetCaseSensitivityMode", "TotalJobs")
                VALUES (
                    {relocationId}, {rootId}, {"/library"}, {"/new-library"},
                    {nameof(RootFolderRelocationMode.MetadataOnly)},
                    {nameof(RootFolderRelocationStatus.Completed)},
                    {"Deleted Library"}, {true}, {DateTime.UtcNow}, {DateTime.UtcNow},
                    {DateTime.UtcNow}, {0}, {false},
                    {nameof(FileSystemCaseSensitivityMode.Auto)},
                    {nameof(FileSystemCaseSensitivityMode.Auto)}, {0});
                """);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"RootFolders\" WHERE \"Id\" = {rootId};");
            context.ChangeTracker.Clear();

            await Assert.ThrowsAsync<SqliteException>(() =>
                migrator.MigrateAsync("20260708224900_AddRootFolderRelocationSkippedItems"));

            Assert.Equal(
                DBNull.Value,
                await ExecuteScalarAsync(
                    connection,
                    "SELECT \"RootFolderId\" FROM \"RootFolderRelocations\" LIMIT 1;"));
            Assert.Equal(
                0L,
                (long)(await ExecuteScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_foreign_key_check;"))!);
            Assert.Equal(
                "ok",
                (await ExecuteScalarAsync(connection, "PRAGMA integrity_check;"))?.ToString());

            await migrator.MigrateAsync();
            Assert.Contains(
                "20260708225144_SetRootFolderRelocationRootDeleteBehavior",
                await context.Database.GetAppliedMigrationsAsync());
        }

        [Fact]
        [Trait("Scenario", "NullableRelocationRootDowngradePreservesValidHistory")]
        public async Task NullableRelocationRoot_DowngradePreservesHistoryWithExistingRoot()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260708225144_SetRootFolderRelocationRootDeleteBehavior");

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "RootFolders" ("Name", "Path", "IsDefault", "CreatedAt")
                VALUES ({"Retained Library"}, {"/library"}, {true}, {DateTime.UtcNow});
                """);
            var rootId = (long)(await ExecuteScalarAsync(
                connection,
                "SELECT last_insert_rowid();"))!;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "RootFolderRelocations" (
                    "Id", "RootFolderId", "SourcePath", "TargetPath", "Mode", "Status",
                    "DesiredName", "DesiredIsDefault", "CompletedAt", "CreatedAt",
                    "UpdatedAt", "CompletedJobs", "DeleteEmptySource",
                    "SourceCaseSensitivityMode", "TargetCaseSensitivityMode", "TotalJobs")
                VALUES (
                    {Guid.NewGuid()}, {rootId}, {"/library"}, {"/new-library"},
                    {nameof(RootFolderRelocationMode.MetadataOnly)},
                    {nameof(RootFolderRelocationStatus.Completed)},
                    {"Retained Library"}, {true}, {DateTime.UtcNow}, {DateTime.UtcNow},
                    {DateTime.UtcNow}, {0}, {false},
                    {nameof(FileSystemCaseSensitivityMode.Auto)},
                    {nameof(FileSystemCaseSensitivityMode.Auto)}, {0});
                """);

            await migrator.MigrateAsync("20260708224900_AddRootFolderRelocationSkippedItems");

            await using (var rootIdCommand = connection.CreateCommand())
            {
                rootIdCommand.CommandText =
                    "SELECT \"RootFolderId\" FROM \"RootFolderRelocations\" LIMIT 1;";
                Assert.Equal(rootId, await rootIdCommand.ExecuteScalarAsync());
            }

            await using (var foreignKeyCheck = connection.CreateCommand())
            {
                foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await foreignKeyCheck.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync());
            }

            await migrator.MigrateAsync();
            Assert.Contains(
                "20260708225144_SetRootFolderRelocationRootDeleteBehavior",
                await context.Database.GetAppliedMigrationsAsync());
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

        private static async Task<string?> GetOwnershipRootForeignKeyDeleteBehaviorAsync(
            SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "on_delete"
                FROM pragma_foreign_key_list('LibraryDirectoryOwnerships')
                WHERE "table" = 'RootFolders'
                  AND "from" = 'ManagedRootFolderId'
                """;
            return (await command.ExecuteScalarAsync())?.ToString();
        }

        private static async Task<object?> ExecuteScalarAsync(
            SqliteConnection connection,
            string commandText)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            return await command.ExecuteScalarAsync();
        }

        private static async Task<bool> TableExistsAsync(
            SqliteConnection connection,
            string tableName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = $name
                """;
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        }

        private static async Task<bool> ColumnExistsAsync(
            SqliteConnection connection,
            string tableName,
            string columnName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info($table)
                WHERE name = $column
                """;
            command.Parameters.AddWithValue("$table", tableName);
            command.Parameters.AddWithValue("$column", columnName);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        }

        private static async Task<bool> IndexExistsAsync(
            SqliteConnection connection,
            string tableName,
            string indexName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_index_list($table)
                WHERE name = $index
                """;
            command.Parameters.AddWithValue("$table", tableName);
            command.Parameters.AddWithValue("$index", indexName);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        }

        private sealed class InterruptOwnershipRecoveryMigration
            : DbCommandInterceptor
        {
            public bool Enabled { get; set; } = true;

            public override ValueTask<InterceptionResult<int>>
                NonQueryExecutingAsync(
                    DbCommand command,
                    CommandEventData eventData,
                    InterceptionResult<int> result,
                    CancellationToken cancellationToken = default)
            {
                if (Enabled
                    && command.CommandText.Contains(
                        "ALTER TABLE \"RootFolderRelocations\" ADD \"TargetIdentityEnrollmentState\"",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Injected interruption after the ownership foreign-key rebuild.");
                }

                return ValueTask.FromResult(result);
            }
        }
    }
}
