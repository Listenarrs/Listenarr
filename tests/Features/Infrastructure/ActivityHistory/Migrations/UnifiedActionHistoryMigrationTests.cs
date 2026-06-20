/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.ActivityHistory.Migrations
{
    public sealed class UnifiedActionHistoryMigrationTests
    {
        [Fact]
        public async Task Migration_PreservesGeneralAndDownloadHistory()
        {
            await using var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new ListenArrDbContext(options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260503104240_UpdateFileAction");

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO History
                    (AudiobookId, AudiobookTitle, EventType, Message, Source, Timestamp, NotificationSent, Data)
                VALUES
                    (1, 'Existing Book', 'Added', 'Existing event', 'AddNew', '2026-01-01T00:00:00Z', 0, NULL);
                """);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO DownloadHistories
                    (DownloadId, EventType, Status, EventDate, AudiobookId, DownloadClient,
                     DownloadClientId, Protocol, Title, OutputPath, Data, ErrorMessage, WasImported, ImportedAt)
                VALUES
                    ('abc123', 5, 7, '2026-01-02T00:00:00Z', NULL, 'qBittorrent',
                     'client-1', 0, 'Imported Book', '/downloads/book', '{{"Quality":"MP3"}}', NULL, 1, '2026-01-02T00:00:00Z');
                """);

            await migrator.MigrateAsync();

            var history = await db.History.AsNoTracking().OrderBy(h => h.Timestamp).ToListAsync();
            Assert.Equal(2, history.Count);
            Assert.Equal("legacy-history-1", history[0].CorrelationId);
            Assert.Equal(HistoryEvents.Imported, history[1].EventType);
            Assert.Equal("ABC123", history[1].DownloadId);
            Assert.Equal(HistoryOutcome.Succeeded, history[1].Outcome);
        }
    }
}
