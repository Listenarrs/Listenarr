/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

/// <summary>
/// Upgrades and rolls back a database across the cover art setting migration alone, starting
/// from the migration that sorts immediately before it. Later migrations stay out of the way,
/// so the tests keep holding once newer migrations land on top of this one.
/// </summary>
[Trait("Area", "Persistence")]
[Trait("Name", "EmbedCoverArtSettingMigrationTests")]
[Trait("Category", "Infrastructure")]
public class EmbedCoverArtSettingMigrationTests : BaseTests
{
    private const string EmbedCoverArtSettingMigrationId =
        "20261029020330_AddEmbedCoverArtInAudioFilesSetting";
    private const string SettingsTable = "ApplicationSettings";
    private const string EmbedCoverArtColumn = "EmbedCoverArtInAudioFiles";

    [Fact]
    [Trait("Scenario", "UpgradeFromPrecedingMigration")]
    public async Task UpgradeFromPrecedingMigration_AddsDisabledSettingToExistingSettingsRow()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        await context.GetService<IMigrator>().MigrateAsync(PrecedingMigrationId(context));
        await InsertSettingsRowAsync(connection);
        Assert.False(await ColumnExistsAsync(connection, EmbedCoverArtColumn));
        Assert.Equal(
            EmbedCoverArtSettingMigrationId,
            (await context.Database.GetPendingMigrationsAsync()).First());

        await context.GetService<IMigrator>().MigrateAsync(EmbedCoverArtSettingMigrationId);

        Assert.Contains(
            EmbedCoverArtSettingMigrationId,
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0L, await ExecuteScalarAsync(
            connection,
            $"SELECT \"{EmbedCoverArtColumn}\" FROM \"{SettingsTable}\" WHERE \"Id\" = 1"));
    }

    [Fact]
    [Trait("Scenario", "RollbackToPrecedingMigration")]
    public async Task RollbackToPrecedingMigration_RemovesSettingAndItsHistoryRow()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        await context.Database.MigrateAsync();
        Assert.True(await ColumnExistsAsync(connection, EmbedCoverArtColumn));

        await context.GetService<IMigrator>().MigrateAsync(PrecedingMigrationId(context));

        Assert.False(await ColumnExistsAsync(connection, EmbedCoverArtColumn));
        Assert.DoesNotContain(
            EmbedCoverArtSettingMigrationId,
            await context.Database.GetAppliedMigrationsAsync());
    }

    private static string PrecedingMigrationId(ListenArrDbContext context)
    {
        var migrations = context.Database.GetMigrations().ToList();
        Assert.Contains(EmbedCoverArtSettingMigrationId, migrations);
        return migrations
            .Where(id => string.CompareOrdinal(id, EmbedCoverArtSettingMigrationId) < 0)
            .Max(StringComparer.Ordinal)!;
    }

    private static DbContextOptions<ListenArrDbContext> CreateOptions(
        SqliteConnection connection) =>
        new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
            .Options;

    // Fills every required column without a default, so the row stands in for the settings a
    // user already saved before upgrading.
    private static async Task InsertSettingsRowAsync(SqliteConnection connection)
    {
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText =
            $"SELECT name, type FROM pragma_table_info('{SettingsTable}') " +
            "WHERE \"notnull\" = 1 AND dflt_value IS NULL AND name <> 'Id'";
        var columns = new List<(string Name, string Value)>();
        await using (var reader = await columnsCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var isText = reader.GetString(1).Equals("TEXT", StringComparison.OrdinalIgnoreCase);
                columns.Add((reader.GetString(0), isText ? "''" : "0"));
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            $"INSERT INTO \"{SettingsTable}\" (\"Id\"{string.Concat(columns.Select(c => $", \"{c.Name}\""))}) " +
            $"VALUES (1{string.Concat(columns.Select(c => $", {c.Value}"))})";
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string column) =>
        await ExecuteScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM pragma_table_info('{SettingsTable}') WHERE name = '{column}'") == 1L;

    private static async Task<long> ExecuteScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
