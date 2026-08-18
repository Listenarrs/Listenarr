/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

/// <summary>
/// Exercises the real SQLite migration pipeline. These tests intentionally
/// validate final migration contracts rather than intermediate PR-only schema.
/// </summary>
[Trait("Area", "Persistence")]
[Trait("Name", "SqliteMigrationSchemaTests")]
[Trait("Category", "Infrastructure")]
public class SqliteMigrationSchemaTests : BaseTests
{
    private const string CanaryMigrationFrontierId =
        "20260621002226_AddApplicationSettingsConcurrency";
    private const string MoveJobSourcePathRepairId =
        "20251124102000_AddMoveJobSourcePath";
    private const string ProcessExecutionLogRepairId =
        "20260809121006_AddProcessExecutionLogs";
    private const string ConsolidatedMigrationId =
        "20260810160602_AddDurableFilesystemRecovery";
    private const string MoveJobRelocationForeignKeyMigrationId =
        "20260810160640_AddMoveJobRelocationForeignKey";
    private const string FileMutationParentGenerationProofsMigrationId =
        "20260818132300_AddFileMutationParentGenerationProofs";

    private static (SqliteConnection Connection, ListenArrDbContext Context)
        CreateMigratedSqliteContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var context = new ListenArrDbContext(CreateOptions(connection));
        context.Database.Migrate();
        return (connection, context);
    }

    private static DbContextOptions<ListenArrDbContext> CreateOptions(
        SqliteConnection connection) =>
        new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
            .Options;

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
                continue;
            }

            var storeObject = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(
                tableName,
                entityType.GetSchema());
            var columns = entityType.GetProperties()
                .Select(property => property.GetColumnName(storeObject))
                .Where(column => !string.IsNullOrEmpty(column))
                .Distinct()
                .ToList();
            if (columns.Count == 0)
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"\"{column}\""))} FROM \"{tableName}\" LIMIT 0";
            try
            {
                using var reader = command.ExecuteReader();
            }
            catch (SqliteException exception)
            {
                failures.Add($"{tableName}: {exception.Message}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "The EF model maps columns absent from the migrated SQLite schema:\n"
            + string.Join("\n", failures));
    }

    [Fact]
    [Trait("Scenario", "PullRequestMigrationsHaveNoNonTransactionalOperationWarnings")]
    public async Task PullRequestMigrations_AfterCanaryFrontier_HaveNoNonTransactionalOperationWarnings()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var baseline = new ListenArrDbContext(CreateOptions(connection)))
        {
            await baseline.GetService<IMigrator>().MigrateAsync(CanaryMigrationFrontierId);
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
        await using var context = new ListenArrDbContext(CreateOptions(connection));

        await context.Database.MigrateAsync();

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The configured EF model differs from the final migration snapshot.");
    }

    [Fact]
    [Trait("Scenario", "FinalMigrationHistoryIsConsolidated")]
    public async Task MigrationHistory_ContainsOnlyRetainedRepairsAndConsolidatedPrMigrationAfterCanary()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));

        await context.Database.MigrateAsync();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        var postCanary = applied
            .Where(id => string.CompareOrdinal(id, CanaryMigrationFrontierId) > 0)
            .ToArray();

        Assert.Equal(
            [
                ProcessExecutionLogRepairId,
                ConsolidatedMigrationId,
                MoveJobRelocationForeignKeyMigrationId,
                FileMutationParentGenerationProofsMigrationId
            ],
            postCanary);
        Assert.Contains("20251124102000_AddMoveJobSourcePath", applied);
    }

    [Fact]
    [Trait("Scenario", "ExactCanaryUpgrade")]
    public async Task ExactCanarySchema_UpgradesAndFencesReleasedActiveMoveJobs()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var canary = new ListenArrDbContext(CreateOptions(connection)))
        {
            await canary.GetService<IMigrator>().MigrateAsync(CanaryMigrationFrontierId);
        }

        // Exact canary did not discover AddMoveJobSourcePath because it shipped
        // without migration metadata. Recreate that released schema/history gap.
        await ExecuteNonQueryAsync(
            connection,
            $"""
            ALTER TABLE "MoveJobs" DROP COLUMN "SourcePath";
            DELETE FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = '{MoveJobSourcePathRepairId}';
            """);
        Assert.False(await ColumnExistsAsync(connection, "MoveJobs", "SourcePath"));
        Assert.False(await TableExistsAsync(connection, "ProcessExecutionLogs"));

        var queuedId = Guid.NewGuid();
        var processingId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        await InsertCanaryMoveJobAsync(connection, queuedId, 1001, "Queued", "1001:queued");
        await InsertCanaryMoveJobAsync(connection, processingId, 1002, "Processing", "1002:processing");
        await InsertCanaryMoveJobAsync(connection, completedId, 1003, "Completed", null);
        await InsertCanaryMoveJobAsync(connection, failedId, 1004, "Failed", null);

        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(options =>
            options.UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name)));
        await using var provider = services.BuildServiceProvider();
        provider.ApplyListenarrDatabaseMigrations();
        var factory = provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var upgraded = await factory.CreateDbContextAsync();

        Assert.True(await ColumnExistsAsync(connection, "MoveJobs", "SourcePath"));
        Assert.True(await TableExistsAsync(connection, "ProcessExecutionLogs"));
        Assert.True(await TableExistsAsync(connection, "AudiobookDeletionIntents"));
        Assert.True(await ColumnExistsAsync(connection, "FileMutationJournals", "AudiobookFileId"));
        Assert.Equal(
            ("NeedsAttention", "Verification", 0, null),
            await ReadMoveJobUpgradeStateAsync(connection, queuedId));
        Assert.Equal(
            ("NeedsAttention", "Verification", 0, null),
            await ReadMoveJobUpgradeStateAsync(connection, processingId));
        Assert.Equal(
            ("Completed", "None", 0, (string?)null),
            await ReadMoveJobUpgradeStateAsync(connection, completedId));
        Assert.Equal(
            ("Failed", "None", 0, (string?)null),
            await ReadMoveJobUpgradeStateAsync(connection, failedId));

        var materialized = await upgraded.MoveJobs
            .OrderBy(job => job.AudiobookId)
            .ToListAsync();
        Assert.Equal(4, materialized.Count);
        Assert.False(upgraded.Database.HasPendingModelChanges());
    }

    [Fact]
    [Trait("Scenario", "ConsolidatedMigrationDowngradeReapply")]
    public async Task ConsolidatedMigration_DowngradesOneStepAndReappliesCleanly()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        Assert.True(await TableExistsAsync(connection, "FileMutationJournals"));
        Assert.True(await ColumnExistsAsync(connection, "MoveJobs", "ExecutionProtocolVersion"));
        Assert.True(await TableExistsAsync(connection, "AudiobookDeletionIntents"));
        Assert.True(await ColumnExistsAsync(connection, "FileMutationJournals", "AudiobookFileId"));

        await migrator.MigrateAsync(ConsolidatedMigrationId);
        Assert.True(await TableExistsAsync(connection, "AudiobookDeletionIntents"));
        Assert.True(await TableExistsAsync(connection, "FileMutationJournals"));
        Assert.True(await ColumnExistsAsync(connection, "FileMutationJournals", "AudiobookFileId"));
        Assert.True(await ColumnExistsAsync(connection, "MoveJobs", "ExecutionProtocolVersion"));
        Assert.False(await ForeignKeyHasDeleteActionAsync(
            connection,
            "MoveJobs",
            "RootFolderRelocations",
            "RelocationId",
            "RESTRICT"));

        await migrator.MigrateAsync(ProcessExecutionLogRepairId);
        Assert.False(await TableExistsAsync(connection, "AudiobookDeletionIntents"));
        Assert.False(await TableExistsAsync(connection, "FileMutationJournals"));
        Assert.False(await ColumnExistsAsync(connection, "MoveJobs", "ExecutionProtocolVersion"));
        Assert.True(await ColumnExistsAsync(connection, "MoveJobs", "SourcePath"));
        Assert.True(await TableExistsAsync(connection, "ProcessExecutionLogs"));

        await migrator.MigrateAsync();
        Assert.True(await TableExistsAsync(connection, "FileMutationJournals"));
        Assert.True(await ColumnExistsAsync(connection, "MoveJobs", "ExecutionProtocolVersion"));
        Assert.True(await TableExistsAsync(connection, "AudiobookDeletionIntents"));
        Assert.True(await ColumnExistsAsync(connection, "FileMutationJournals", "AudiobookFileId"));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    [Trait("Scenario", "PathIdentityDefaultSentinels")]
    public async Task ExplicitValidPathIdentity_IsNotReplacedByUpgradeDefaultsOnInsert()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        await context.Database.MigrateAsync();

        var rootPath = Path.Join(Path.GetTempPath(), $"sentinel-root-{Guid.NewGuid():N}");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var root = new RootFolder
        {
            Name = "Sentinel Root",
            Path = rootPath,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            ResolvedCaseSensitivity = semantics.CaseSensitivity,
            PathIdentityKey = $"sentinel-root-{Guid.NewGuid():N}",
            PathIdentityState = PathIdentityState.Valid
        };
        var audiobook = new Audiobook
        {
            Title = "Sentinel Audiobook",
            BasePath = Path.Join(rootPath, "Author", "Title")
        };
        context.RootFolders.Add(root);
        context.Audiobooks.Add(audiobook);
        await context.SaveChangesAsync();

        var filePath = Path.Join(audiobook.BasePath!, "book.m4b");
        var trackedFile = AudiobookFile.CreateUnresolved(filePath);
        trackedFile.AudiobookId = audiobook.Id;
        trackedFile.ApplyPathIdentity(
            filePath,
            AudiobookFilePathIdentity.CreateValid(
                filePath,
                semantics,
                FileSystemCaseSensitivityMode.Auto,
                rootPath));
        context.AudiobookFiles.Add(trackedFile);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var persistedRoot = await context.RootFolders.SingleAsync();
        var persistedFile = await context.AudiobookFiles.SingleAsync();
        Assert.Equal(PathIdentityState.Valid, persistedRoot.PathIdentityState);
        Assert.Equal(PathIdentityState.Valid, persistedFile.PathIdentityState);
        Assert.Equal(semantics.CaseSensitivity, persistedRoot.ResolvedCaseSensitivity);
        Assert.Equal(semantics.CaseSensitivity, persistedFile.PathCaseSensitivity);
    }

    [Fact]
    [Trait("Scenario", "FinalSchemaContracts")]
    public async Task FinalSchema_HasDurableDefaultsIndexesAndSetNullOwnershipRootForeignKey()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        await context.Database.MigrateAsync();

        Assert.Equal("0", await ColumnDefaultAsync(connection, "MoveJobs", "ExecutionProtocolVersion"));
        Assert.Equal("'None'", await ColumnDefaultAsync(connection, "MoveJobs", "FailureKind"));
        Assert.Equal("'Auto'", await ColumnDefaultAsync(connection, "RootFolders", "CaseSensitivityMode"));
        Assert.Equal("'Unknown'", await ColumnDefaultAsync(connection, "RootFolders", "ResolvedCaseSensitivity"));
        Assert.Equal("'Unavailable'", await ColumnDefaultAsync(connection, "RootFolders", "PathIdentityState"));
        Assert.Equal("'Auto'", await ColumnDefaultAsync(connection, "AudiobookFiles", "PathCaseSensitivityMode"));
        Assert.Equal("'Unknown'", await ColumnDefaultAsync(connection, "AudiobookFiles", "PathCaseSensitivity"));
        Assert.Equal("'Unavailable'", await ColumnDefaultAsync(connection, "AudiobookFiles", "PathIdentityState"));

        Assert.True(await IndexExistsAsync(connection, "IX_RootFolders_SingleDefault"));
        Assert.True(await IndexExistsAsync(connection, "IX_AudiobookFiles_PathOwnershipKey"));
        Assert.True(await IndexExistsAsync(connection, "IX_LibraryDirectoryOwnerships_PathOwnershipKey"));
        Assert.True(await ForeignKeyHasDeleteActionAsync(
            connection,
            "LibraryDirectoryOwnerships",
            "RootFolders",
            "ManagedRootFolderId",
            "SET NULL"));
        Assert.True(await ForeignKeyHasDeleteActionAsync(
            connection,
            "MoveJobs",
            "RootFolderRelocations",
            "RelocationId",
            "RESTRICT"));
    }

    [Fact]
    [Trait("Scenario", "MoveJobsSourcePathRepair")]
    public async Task MoveJobs_SourcePathColumn_ExistsAfterMigrate()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));

        await context.Database.MigrateAsync();

        Assert.True(await ColumnExistsAsync(connection, "MoveJobs", "SourcePath"));
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertCanaryMoveJobAsync(
        SqliteConnection connection,
        Guid id,
        int audiobookId,
        string status,
        string? activeDeduplicationKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "MoveJobs"
                ("Id", "AudiobookId", "RequestedPath", "EnqueuedAt", "Status",
                 "Error", "AttemptCount", "UpdatedAt", "ActiveDeduplicationKey")
            VALUES
                ($id, $audiobookId, $requestedPath, CURRENT_TIMESTAMP, $status,
                 NULL, 0, CURRENT_TIMESTAMP, $activeDeduplicationKey);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$audiobookId", audiobookId);
        command.Parameters.AddWithValue("$requestedPath", $"/library/{audiobookId}");
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$activeDeduplicationKey",
            activeDeduplicationKey is null ? DBNull.Value : activeDeduplicationKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string Status, string FailureKind, int Protocol, string? ActiveDeduplicationKey)>
        ReadMoveJobUpgradeStateAsync(
            SqliteConnection connection,
            Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Status", "FailureKind", "ExecutionProtocolVersion", "ActiveDeduplicationKey"
            FROM "MoveJobs"
            WHERE "Id" = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name";
        command.Parameters.AddWithValue("$name", column);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<string?> ColumnDefaultAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT dflt_value FROM pragma_table_info('{table}') WHERE name=$name";
        command.Parameters.AddWithValue("$name", column);
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> IndexExistsAsync(
        SqliteConnection connection,
        string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name";
        command.Parameters.AddWithValue("$name", index);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> ForeignKeyHasDeleteActionAsync(
        SqliteConnection connection,
        string table,
        string principalTable,
        string fromColumn,
        string deleteAction)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_foreign_key_list('{table}') WHERE \"table\"=$principal AND \"from\"=$column AND on_delete=$delete";
        command.Parameters.AddWithValue("$principal", principalTable);
        command.Parameters.AddWithValue("$column", fromColumn);
        command.Parameters.AddWithValue("$delete", deleteAction);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }
}
