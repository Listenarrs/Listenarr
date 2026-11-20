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

namespace Listenarr.Tests.Features.Infrastructure.Persistence.LargeLibrary;

/// <summary>
/// Which rows of the large library sit at the oldest rescan due times. Rows
/// that this host can never rescan pile up there in a real database, because
/// nothing on the old code advanced them.
/// </summary>
public enum LargeLibraryVariant
{
    // Every due time is spread across the window around the clock.
    Main,

    // Files whose stored path syntax is foreign hold the oldest due times.
    ForeignSyntaxOldest,

    // Relative files with no stored syntax under a foreign base path hold
    // the oldest due times.
    ForeignBaseOldest
}

public sealed partial class LargeLibraryFixtureBuilder
{
    // The schedule draws use a stream of their own, so stamping leaves the
    // rows that BuildAsync generated unchanged.
    private const int RescanScheduleSeedOffset = 1;
    private static readonly TimeSpan DueWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Stamps every file's metadata rescan due time on a fixture this builder
    /// built. Call it after <see cref="BuildAsync"/>; the same variant, clock
    /// and seed give the same due times.
    /// </summary>
    public async Task<LargeLibraryRescanScheduleCounts> StampRescanScheduleAsync(
        LargeLibraryFixture fixture,
        LargeLibraryVariant variant = LargeLibraryVariant.Main,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE "AudiobookFiles" SET "MetadataRescanDueUtc" = $due WHERE "Id" = $id;
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var due = command.Parameters.Add("$due", SqliteType.Text);
        await command.PrepareAsync(cancellationToken);

        var random = new Random(_seed + RescanScheduleSeedOffset);
        var counts = new LargeLibraryRescanScheduleCounts();
        var oldestDue = ClockUtc - DueWindow - DueWindow;
        foreach (var file in GenerateFiles())
        {
            var offset = TimeSpan.FromSeconds(random.NextDouble() * DueWindow.TotalSeconds);
            DateTime dueUtc;
            if ((file.ForeignSyntax && variant == LargeLibraryVariant.ForeignSyntaxOldest)
                || (file.ForeignBase && variant == LargeLibraryVariant.ForeignBaseOldest))
            {
                dueUtc = oldestDue.AddSeconds(file.Index);
            }
            else
            {
                // Half of the rows, so half of those missing metadata, are
                // due in the future.
                dueUtc = random.Next(2) == 0 ? ClockUtc - offset : ClockUtc + offset;
            }

            id.Value = FileId(file.Index);
            due.Value = dueUtc;
            await command.ExecuteNonQueryAsync(cancellationToken);
            counts.Record(file.MissingMetadata, file.ForeignSyntax, file.ForeignBase, dueUtc <= ClockUtc);
        }

        await transaction.CommitAsync(cancellationToken);
        return counts;
    }
}

/// <summary>Due-time counts of a stamped large library, for test assertions.</summary>
public sealed class LargeLibraryRescanScheduleCounts
{
    public int DueMissingMetadata { get; private set; }
    public int DueMissingForeignSyntax { get; private set; }
    public int DueMissingForeignBase { get; private set; }

    internal void Record(bool missing, bool foreignSyntax, bool foreignBase, bool due)
    {
        if (!missing || !due)
        {
            return;
        }

        DueMissingMetadata++;
        if (foreignSyntax)
        {
            DueMissingForeignSyntax++;
        }
        else if (foreignBase)
        {
            DueMissingForeignBase++;
        }
    }
}
