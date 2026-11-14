/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

/// <summary>
/// Upgrades a database holding files from before the rescan schedule existed,
/// and downgrades it again, through the real SQLite migration pipeline.
/// </summary>
[Trait("Name", "MetadataRescanScheduleMigrationTests")]
[Trait("Category", "Persistence")]
public sealed class MetadataRescanScheduleMigrationTests : BaseTests
{
    private const string PreviousMigrationId = "20261029020400_AddReleaseBlocklist";
    private const string ScheduleMigrationId = "20261114004926_AddMetadataRescanSchedule";
    private const string ScheduleIndexName = "IX_AudiobookFiles_MetadataRescanDue";
    private static readonly string[] ScheduleColumns =
    [
        "MetadataRescanDueUtc",
        "MetadataRescanFailureCount",
        "MetadataRescanUnavailableCount",
        "MetadataRescanBlockedCount",
        "MetadataRescanLastOutcome"
    ];

    private readonly ITestOutputHelper _output;

    public MetadataRescanScheduleMigrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // The migration is a pure EF scaffold (#717), so it repairs no data: every file that
    // existed before it takes the scaffolded default due time. All of them are due at once
    // and the due page takes them in id order, not creation order; each file then moves to
    // its own due time as the rescan processes it.
    [Fact]
    public async Task Upgrade_LeavesExistingFilesDueAtOnceInIdOrderAndBuildsTheFilteredIndex()
    {
        await using var connection = await OpenAtPreviousMigrationAsync();
        await SeedFilesAsync(connection);

        await MigrateAsync(connection, ScheduleMigrationId);

        Assert.Equal(
            [
                ("0001-01-01 00:00:00", 0L, 0L, 0L, (string?)null),
                ("0001-01-01 00:00:00", 0L, 0L, 0L, (string?)null)
            ],
            await ReadScheduleAsync(connection));
        Assert.Equal(
            "CREATE INDEX \"IX_AudiobookFiles_MetadataRescanDue\" ON \"AudiobookFiles\" (\"MetadataRescanDueUtc\") "
            + "WHERE \"DurationSeconds\" IS NULL OR \"Format\" IS NULL OR \"SampleRate\" IS NULL OR \"Size\" IS NULL OR \"Size\" = 64",
            await IndexSqlAsync(connection));

        // Due even at a time before either file was created.
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        var page = await new EfAudiobookFileRepository(context).GetMetadataRescanDuePageAsync(
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), [], pageSize: 10);
        Assert.Equal([1, 2], page.Select(candidate => candidate.File.Id));
    }

    [Fact]
    public async Task Downgrade_DropsTheIndexAndColumnsAndKeepsTheFiles()
    {
        await using var connection = await OpenAtPreviousMigrationAsync();
        await SeedFilesAsync(connection);
        await MigrateAsync(connection, ScheduleMigrationId);
        await using (var context = new ListenArrDbContext(CreateOptions(connection)))
        {
            _output.WriteLine(context.GetService<IMigrator>().GenerateScript(ScheduleMigrationId, PreviousMigrationId));
        }

        await MigrateAsync(connection, PreviousMigrationId);

        Assert.Null(await IndexSqlAsync(connection));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('AudiobookFiles')";
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Empty(columns.Intersect(ScheduleColumns));
        command.CommandText = "SELECT group_concat(\"Path\", ',') FROM (SELECT \"Path\" FROM \"AudiobookFiles\" ORDER BY \"Id\")";
        Assert.Equal("first.m4b,second.m4b", await command.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenAtPreviousMigrationAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await MigrateAsync(connection, PreviousMigrationId);
        return connection;
    }

    private static async Task MigrateAsync(SqliteConnection connection, string targetMigration)
    {
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static DbContextOptions<ListenArrDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
            .Options;

    // Two files as the previous schema stored them, the later id created first.
    private static async Task SeedFilesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Audiobooks" ("Id", "Title", "Monitored", "Explicit", "Abridged")
            VALUES (1, $title, 1, 0, 0);
            INSERT INTO "AudiobookFiles" ("Id", "AudiobookId", "Path", "CreatedAt")
            VALUES (1, 1, 'first.m4b', '2025-06-15 21:05:09.1234567'),
                   (2, 1, 'second.m4b', '2025-05-01 08:30:00');
            """;
        command.Parameters.AddWithValue("$title", "A Princess of Mars");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string Due, long Failures, long Unavailable, long Blocked, string? Outcome)>>
        ReadScheduleAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "MetadataRescanDueUtc", "MetadataRescanFailureCount",
                   "MetadataRescanUnavailableCount", "MetadataRescanBlockedCount",
                   "MetadataRescanLastOutcome"
            FROM "AudiobookFiles" ORDER BY "Id";
            """;
        var rows = new List<(string, long, long, long, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static async Task<string?> IndexSqlAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", ScheduleIndexName);
        return (string?)await command.ExecuteScalarAsync();
    }
}
