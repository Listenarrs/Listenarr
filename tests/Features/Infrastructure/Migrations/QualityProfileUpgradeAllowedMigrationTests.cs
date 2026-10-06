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

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

/// <summary>
/// UpgradeAllowed has to inherit what each stored profile already meant. Until this column
/// existed, a blank CutoffQuality was the only way to record "do not upgrade this", so a blank
/// cutoff has to come out of the upgrade with upgrades off, and a profile that names a cutoff has
/// to come out with them on.
///
/// Getting that backwards is silent. Nothing fails, nothing is logged, and the first sign of it is
/// an install downloading replacements for books the owner had told it to leave alone. Both
/// directions are pinned here, and each is the other's control.
/// </summary>
[Trait("Area", "Persistence")]
[Trait("Name", "QualityProfileUpgradeAllowedMigrationTests")]
[Trait("Category", "Infrastructure")]
public sealed class QualityProfileUpgradeAllowedMigrationTests : BaseTests
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PreviousMigrationId = "20260825021432_AddWeakStorageVerifiedCleanup";

    /// <summary>
    /// Every cutoff the engine reads as blank has to be backfilled as blank. The cases past the
    /// space are not decoration: SQLite's one-argument trim() strips U+0020 only, so a backfill
    /// written in SQL turns upgrades ON for a tab or a newline while the engine goes on treating
    /// the profile as not upgrading. The space case is the control that such a backfill passes.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\u00a0")]
    public async Task ProfileWithABlankCutoff_ComesOutWithUpgradesOff_AndKeepsItsBlankCutoff(
        string? storedCutoff)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var legacy = new ListenArrDbContext(CreateOptions(connection)))
        {
            await legacy.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
            Assert.False(await ColumnExistsAsync(connection, "QualityProfiles", "UpgradeAllowed"));
            await InsertLegacyProfileAsync(legacy, "Upgrades off", storedCutoff);
        }

        await MigrateAndRepairAsync(connection);

        await using var upgraded = new ListenArrDbContext(CreateOptions(connection));
        var profile = await upgraded.QualityProfiles.AsNoTracking()
            .SingleAsync(candidate => candidate.Name == "Upgrades off");

        Assert.False(profile.UpgradeAllowed);
        Assert.True(string.IsNullOrWhiteSpace(profile.CutoffQuality));
    }

    /// <summary>
    /// The control for the case above. Same upgrade, same code path, opposite answer, so a
    /// migration that simply wrote false everywhere would fail here.
    /// </summary>
    [Fact]
    public async Task ProfileWithARealCutoff_ComesOutWithUpgradesOn_AndKeepsItsCutoff()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var legacy = new ListenArrDbContext(CreateOptions(connection)))
        {
            await legacy.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
            await InsertLegacyProfileAsync(legacy, "Upgrades on", "AAC 256kbps");
        }

        await MigrateAndRepairAsync(connection);

        await using var upgraded = new ListenArrDbContext(CreateOptions(connection));
        var profile = await upgraded.QualityProfiles.AsNoTracking()
            .SingleAsync(candidate => candidate.Name == "Upgrades on");

        Assert.True(profile.UpgradeAllowed);
        Assert.Equal("AAC 256kbps", profile.CutoffQuality);
    }

    /// <summary>
    /// Both shapes in one database, upgraded together, because the failure this guards against is
    /// a backfill that writes one value across the whole table.
    /// </summary>
    [Fact]
    public async Task MixedProfiles_EachKeepTheirOwnMeaning()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var legacy = new ListenArrDbContext(CreateOptions(connection)))
        {
            await legacy.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
            await InsertLegacyProfileAsync(legacy, "Blank", null);
            await InsertLegacyProfileAsync(legacy, "Empty", "");
            await InsertLegacyProfileAsync(legacy, "Named", "MP3 320kbps");
        }

        await MigrateAndRepairAsync(connection);

        await using var upgraded = new ListenArrDbContext(CreateOptions(connection));
        var flags = await upgraded.QualityProfiles.AsNoTracking()
            .ToDictionaryAsync(profile => profile.Name, profile => profile.UpgradeAllowed);

        Assert.False(flags["Blank"]);
        Assert.False(flags["Empty"]);
        Assert.True(flags["Named"]);
    }

    /// <summary>
    /// The backfill runs on every start, not once, so it has to leave alone the state that only
    /// became expressible with this column: upgrades off with a real cutoff still named. A second
    /// pass that reads the cutoff and switches upgrades back on would undo the owner's choice
    /// without anything to show for it.
    /// </summary>
    [Fact]
    public async Task SecondStartup_DoesNotDisturbAProfileSavedWithUpgradesOffAndACutoff()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var fresh = new ListenArrDbContext(CreateOptions(connection)))
        {
            await fresh.Database.MigrateAsync();
            fresh.QualityProfiles.Add(new QualityProfile
            {
                Name = "Off but pinned",
                UpgradeAllowed = false,
                CutoffQuality = "AAC 256kbps",
                Qualities = BuildLadder()
            });
            await fresh.SaveChangesAsync();
        }

        // The value survives the insert at all: the column defaults to 1 in the database, so a
        // provider that skipped writing false would store the default and this would read true.
        await using (var firstRead = new ListenArrDbContext(CreateOptions(connection)))
        {
            var stored = await firstRead.QualityProfiles.AsNoTracking().SingleAsync();
            Assert.False(stored.UpgradeAllowed);
            Assert.Equal("AAC 256kbps", stored.CutoffQuality);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await MigrateAndRepairAsync(connection);
        }

        await using var afterRestarts = new ListenArrDbContext(CreateOptions(connection));
        var profile = await afterRestarts.QualityProfiles.AsNoTracking().SingleAsync();

        Assert.False(profile.UpgradeAllowed);
        Assert.Equal("AAC 256kbps", profile.CutoffQuality);
    }

    /// <summary>
    /// Control for the repair itself: it is not a no-op that happens to leave everything alone. A
    /// row that contradicts itself, upgrades on with no cutoff to upgrade towards, is corrected.
    /// </summary>
    [Fact]
    public async Task Repair_TurnsUpgradesOffOnARowThatContradictsItself()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var fresh = new ListenArrDbContext(CreateOptions(connection)))
        {
            await fresh.Database.MigrateAsync();
            fresh.QualityProfiles.Add(new QualityProfile
            {
                Name = "On with nothing named",
                UpgradeAllowed = true,
                CutoffQuality = null,
                Qualities = BuildLadder()
            });
            await fresh.SaveChangesAsync();
        }

        await using var repairContext = new ListenArrDbContext(CreateOptions(connection));
        var repaired = ListenarrDatabaseMigrationPreflight.RepairPostMigrationData(repairContext);

        Assert.Equal(1, repaired.QualityProfileUpgradeFlagsRepaired);

        await using var reread = new ListenArrDbContext(CreateOptions(connection));
        Assert.False((await reread.QualityProfiles.AsNoTracking().SingleAsync()).UpgradeAllowed);
    }

    /// <summary>
    /// Rolling the migration back drops the column and leaves the cutoffs alone, which is what a
    /// plain EF scaffold does and all it is allowed to do here: post-canary migrations have to
    /// stay direct scaffolds, so Down cannot carry the flag back into the cutoff with SQL of its
    /// own (tests/Features/Architecture/MigrationProvenanceArchitectureTests.cs:60-66).
    ///
    /// The cost is worth stating rather than discovering. A profile saved as upgrades-off while
    /// still naming a cutoff is a state only the newer code can produce, and on the older schema
    /// that same row reads as upgrades on. Anyone rolling back has to blank those cutoffs by hand.
    /// Profiles that were never edited after the upgrade are unaffected, because their cutoff is
    /// still the blank one they always had.
    /// </summary>
    [Fact]
    public async Task Downgrade_DropsTheColumnAndLeavesCutoffsAsTheyWere()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var fresh = new ListenArrDbContext(CreateOptions(connection)))
        {
            await fresh.Database.MigrateAsync();
            fresh.QualityProfiles.AddRange(
                new QualityProfile
                {
                    Name = "Off but pinned",
                    UpgradeAllowed = false,
                    CutoffQuality = "AAC 256kbps",
                    Qualities = BuildLadder()
                },
                new QualityProfile
                {
                    Name = "Off the old way",
                    UpgradeAllowed = false,
                    CutoffQuality = null,
                    Qualities = BuildLadder()
                },
                new QualityProfile
                {
                    Name = "Still upgrading",
                    UpgradeAllowed = true,
                    CutoffQuality = "MP3 320kbps",
                    Qualities = BuildLadder()
                });
            await fresh.SaveChangesAsync();
        }

        await using (var downgrading = new ListenArrDbContext(CreateOptions(connection)))
        {
            await downgrading.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        Assert.False(await ColumnExistsAsync(connection, "QualityProfiles", "UpgradeAllowed"));

        // The profile that recorded upgrades-off the way the older schema understands it still
        // reads correctly there.
        Assert.Null(await ReadCutoffAsync(connection, "Off the old way"));

        // The two that name a cutoff are indistinguishable on the older schema, which is the
        // caveat this test exists to state.
        Assert.Equal("AAC 256kbps", await ReadCutoffAsync(connection, "Off but pinned"));
        Assert.Equal("MP3 320kbps", await ReadCutoffAsync(connection, "Still upgrading"));
    }

    /// <summary>
    /// Re-applying after a rollback has to put the column back and leave each profile saying what
    /// its cutoff says, so an install that goes down and comes back up is not left broken.
    /// </summary>
    [Fact]
    public async Task ReapplyingAfterADowngrade_RestoresTheColumnFromTheCutoffs()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var fresh = new ListenArrDbContext(CreateOptions(connection)))
        {
            await fresh.Database.MigrateAsync();
            fresh.QualityProfiles.AddRange(
                new QualityProfile
                {
                    Name = "Off the old way",
                    UpgradeAllowed = false,
                    CutoffQuality = null,
                    Qualities = BuildLadder()
                },
                new QualityProfile
                {
                    Name = "Off but pinned",
                    UpgradeAllowed = false,
                    CutoffQuality = "AAC 256kbps",
                    Qualities = BuildLadder()
                },
                new QualityProfile
                {
                    Name = "Still upgrading",
                    UpgradeAllowed = true,
                    CutoffQuality = "MP3 320kbps",
                    Qualities = BuildLadder()
                });
            await fresh.SaveChangesAsync();
        }

        await using (var downgrading = new ListenArrDbContext(CreateOptions(connection)))
        {
            await downgrading.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        await MigrateAndRepairAsync(connection);

        await using var restored = new ListenArrDbContext(CreateOptions(connection));
        var flags = await restored.QualityProfiles.AsNoTracking()
            .ToDictionaryAsync(profile => profile.Name, profile => profile.UpgradeAllowed);

        Assert.False(flags["Off the old way"]);
        Assert.True(flags["Still upgrading"]);

        // And the row whose setting the round trip loses, asserted rather than described. There is
        // nowhere on the older schema for "off, but here is my cutoff" to live, so reapplying
        // re-derives it from the cutoff and gets upgrades back on. Anyone rolling back and
        // forward again has to set those profiles the way they want them.
        Assert.True(flags["Off but pinned"]);
    }

    private static DbContextOptions<ListenArrDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
            .Options;

    /// <summary>
    /// What ApplyListenarrDatabaseMigrations does on a start: apply migrations, then run the
    /// post-migration repair.
    /// </summary>
    private static async Task MigrateAndRepairAsync(SqliteConnection connection)
    {
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        await context.Database.MigrateAsync();
        ListenarrDatabaseMigrationPreflight.RepairPostMigrationData(context);
    }

    private static async Task InsertLegacyProfileAsync(
        ListenArrDbContext context,
        string name,
        string? cutoffQuality)
    {
        // Written as the older schema held it, because the entity now carries a column that
        // database does not have yet.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "QualityProfiles" (
                "Name", "Description", "CutoffQuality", "Qualities",
                "PreferredFormats", "PreferredWords", "MustContain", "MustNotContain",
                "PreferredLanguages", "MinimumSize", "MaximumSize", "MinimumSeeders",
                "MinimumScore", "IsDefault", "PreferNewerReleases", "MaximumAge",
                "CreatedAt", "UpdatedAt")
            VALUES (
                {name}, {null as string}, {cutoffQuality}, {LadderJson},
                {"[]"}, {"[]"}, {"[]"}, {"[]"},
                {"[\"English\"]"}, {0}, {0}, {1},
                {0}, {0}, {1}, {0},
                {DateTime.UtcNow}, {DateTime.UtcNow});
            """);
    }

    private const string LadderJson =
        """
        [{"Quality":"AAC 256kbps","Allowed":true,"Priority":0,"Codec":"AAC","Bitrate":256,"IsLossless":false},
         {"Quality":"MP3 320kbps","Allowed":true,"Priority":1,"Codec":"MP3","Bitrate":320,"IsLossless":false}]
        """;

    private static List<QualityDefinition> BuildLadder() =>
    [
        new() { Quality = "AAC 256kbps", Allowed = true, Priority = 0, Codec = "AAC", Bitrate = 256 },
        new() { Quality = "MP3 320kbps", Allowed = true, Priority = 1, Codec = "MP3", Bitrate = 320 }
    ];

    private static async Task<string?> ReadCutoffAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "CutoffQuality" FROM "QualityProfiles" WHERE "Name" = $name;""";
        command.Parameters.AddWithValue("$name", name);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (string)value;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE "name" = $column;""";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }
}
