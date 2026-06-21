/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

public sealed class ReleasedSchemaUpgradeTests
{
    [Fact]
    public async Task PreviousReleasedSchema_UpgradesToCurrentModel()
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"released-upgrade-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;

        try
        {
            await using (var db = new ListenArrDbContext(options))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync("20260619192820_AddHistoryExternalAudiobookId");
                await migrator.MigrateAsync();
            }

            await using var verified = new ListenArrDbContext(options);
            Assert.Empty(await verified.Database.GetPendingMigrationsAsync());
            var columns = await verified.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('ApplicationSettings')")
                .ToListAsync();
            Assert.Contains("Version", columns);
            var indexes = await verified.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index'")
                .ToListAsync();
            Assert.Contains("IX_MoveJobs_ActiveDeduplicationKey", indexes);
            Assert.Contains("IX_DownloadProcessingJobs_ActiveDeduplicationKey", indexes);
            Assert.Contains("IX_Downloads_ActiveAudiobookDeduplicationKey", indexes);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }
}
