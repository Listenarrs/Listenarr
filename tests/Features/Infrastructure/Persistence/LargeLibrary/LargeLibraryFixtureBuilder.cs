/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.Json;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Infrastructure.Persistence.LargeLibrary;

/// <summary>
/// A book from test-fixtures/large-library-books.json. Every value is an
/// entry of the shared public-domain test corpus, copied verbatim.
/// </summary>
public sealed record LargeLibraryBook(
    string Title,
    string? Subtitle,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Narrators,
    string? Series,
    string Asin);

/// <summary>
/// Generates a representative large library into a file-backed SQLite
/// database migrated by the real migration chain, for query-plan tests.
/// Generation is deterministic: the same clock and seed give the same rows.
/// This file writes only columns of the base schema, so it builds on any
/// branch. Other areas stamp their own columns and add their own tables' rows
/// in further partial files of this class, applied after
/// <see cref="BuildAsync"/> and off unless a test asks.
/// </summary>
public sealed partial class LargeLibraryFixtureBuilder
{
    public const int AudiobookCount = 50_000;
    public const int FilesPerAudiobook = 3;
    public const int FileCount = AudiobookCount * FilesPerAudiobook;

    // One file in 50 (2 percent) stores the other platform's path syntax.
    private const int ForeignSyntaxFileInterval = 50;
    // One book in 100 (1 percent of files) keeps relative files with no stored
    // syntax under a base path in the other platform's syntax.
    private const int ForeignBaseAudiobookInterval = 100;
    // One book in 5 (20 percent) is unmonitored.
    private const int UnmonitoredAudiobookInterval = 5;
    // About 10 percent of host files are missing metadata.
    private const double MissingMetadataShare = 0.10;

    private readonly int _seed;

    public LargeLibraryFixtureBuilder(
        DateTime clockUtc,
        int seed = 1151)
    {
        if (clockUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The fixture clock must be UTC.", nameof(clockUtc));
        }

        ClockUtc = clockUtc;
        _seed = seed;
    }

    public DateTime ClockUtc { get; }

    public static FileSystemPathSyntax HostSyntax => OperatingSystem.IsWindows()
        ? FileSystemPathSyntax.Windows
        : FileSystemPathSyntax.Unix;

    public static FileSystemPathSyntax ForeignSyntax => OperatingSystem.IsWindows()
        ? FileSystemPathSyntax.Unix
        : FileSystemPathSyntax.Windows;

    public static IReadOnlyList<LargeLibraryBook> LoadBooks()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(
            TestUtils.FindRepositoryRoot(),
            "test-fixtures",
            "large-library-books.json")));
        return document.RootElement
            .GetProperty("books")
            .EnumerateArray()
            .Select(book => new LargeLibraryBook(
                book.GetProperty("title").GetString()!,
                book.GetProperty("subtitle").GetString(),
                ReadStrings(book.GetProperty("authors")),
                ReadStrings(book.GetProperty("narrators")),
                book.GetProperty("series").GetString(),
                book.GetProperty("asin").GetString()!))
            .ToList();
    }

    public async Task<LargeLibraryFixture> BuildAsync(CancellationToken cancellationToken = default)
    {
        var books = LoadBooks();
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"listenarr-large-library-{Guid.NewGuid():N}.db");
        var fixture = new LargeLibraryFixture(databasePath, ClockUtc);
        try
        {
            await using (var context = new ListenArrDbContext(fixture.CreateOptions()))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await using var connection = new SqliteConnection(fixture.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await InsertAudiobooksAsync(connection, transaction, books, cancellationToken);
            fixture.Counts = await InsertFilesAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertAudiobooksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LargeLibraryBook> books,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "Audiobooks"
                ("Id", "Title", "Subtitle", "Authors", "Narrators", "Series", "Asin",
                 "BasePath", "Monitored", "Explicit", "Abridged")
            VALUES ($id, $title, $subtitle, $authors, $narrators, $series, $asin,
                 $basePath, $monitored, 0, 0);
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var subtitle = command.Parameters.Add("$subtitle", SqliteType.Text);
        var authors = command.Parameters.Add("$authors", SqliteType.Text);
        var narrators = command.Parameters.Add("$narrators", SqliteType.Text);
        var series = command.Parameters.Add("$series", SqliteType.Text);
        var asin = command.Parameters.Add("$asin", SqliteType.Text);
        var basePath = command.Parameters.Add("$basePath", SqliteType.Text);
        var monitored = command.Parameters.Add("$monitored", SqliteType.Integer);
        await command.PrepareAsync(cancellationToken);

        for (var index = 0; index < AudiobookCount; index++)
        {
            // Books cycle through the corpus; rows are told apart by id only.
            // ASINs are written on the first pass alone, so none is repeated.
            var book = books[index % books.Count];
            id.Value = AudiobookId(index);
            title.Value = book.Title;
            subtitle.Value = (object?)book.Subtitle ?? DBNull.Value;
            authors.Value = JsonSerializer.Serialize(book.Authors);
            narrators.Value = JsonSerializer.Serialize(book.Narrators);
            series.Value = (object?)book.Series ?? DBNull.Value;
            asin.Value = index < books.Count ? book.Asin : DBNull.Value;
            basePath.Value = AudiobookBasePath(index);
            monitored.Value = index % UnmonitoredAudiobookInterval == 0 ? 0 : 1;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<LargeLibraryCounts> InsertFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "AudiobookFiles"
                ("Id", "AudiobookId", "Path", "PathSyntax", "Size", "DurationSeconds",
                 "Format", "SampleRate", "CreatedAt")
            VALUES ($id, $audiobookId, $path, $pathSyntax, $size, $duration,
                 $format, $sampleRate, $createdAt);
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var audiobookId = command.Parameters.Add("$audiobookId", SqliteType.Integer);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var pathSyntax = command.Parameters.Add("$pathSyntax", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var duration = command.Parameters.Add("$duration", SqliteType.Real);
        var format = command.Parameters.Add("$format", SqliteType.Text);
        var sampleRate = command.Parameters.Add("$sampleRate", SqliteType.Integer);
        var createdAt = command.Parameters.Add("$createdAt", SqliteType.Text);
        await command.PrepareAsync(cancellationToken);

        var counts = new LargeLibraryCounts();
        foreach (var file in GenerateFiles())
        {
            id.Value = FileId(file.Index);
            audiobookId.Value = AudiobookId(file.AudiobookIndex);
            if (file.ForeignSyntax)
            {
                path.Value = AbsolutePath(ForeignSyntax, $"file-{file.Index:D6}.m4b");
                pathSyntax.Value = ForeignSyntax.ToString();
            }
            else
            {
                path.Value = $"file-{file.Index:D6}.m4b";
                pathSyntax.Value = file.ForeignBase ? DBNull.Value : HostSyntax.ToString();
            }

            WriteMetadata(file.MissingMetadata ? file.Index : -1, size, duration, format, sampleRate);
            // Files were added one second apart in id order, the last one
            // just before the clock.
            createdAt.Value = ClockUtc.AddSeconds(file.Index - FileCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
            counts.Record(file.MissingMetadata);
        }

        return counts;
    }

    // Every file row in id order. Each call replays the same draws, so a
    // partial file that stamps its own columns sees the rows BuildAsync wrote.
    private IEnumerable<GeneratedFile> GenerateFiles()
    {
        var random = new Random(_seed);
        for (var fileIndex = 0; fileIndex < FileCount; fileIndex++)
        {
            var audiobookIndex = fileIndex / FilesPerAudiobook;
            var foreignSyntax = fileIndex % ForeignSyntaxFileInterval == 0;
            var foreignBase = !foreignSyntax && IsForeignBaseAudiobook(audiobookIndex);
            // Rows this host cannot read are never repaired, so they stay
            // missing metadata; host rows are missing at the stated share.
            var missing = foreignSyntax || foreignBase || random.NextDouble() < MissingMetadataShare;
            yield return new GeneratedFile(fileIndex, audiobookIndex, foreignSyntax, foreignBase, missing);
        }
    }

    // A file missing metadata lacks one field, chosen by its index so every
    // branch of the missing-metadata predicate has rows; -1 means complete.
    private static void WriteMetadata(
        int missingIndex,
        SqliteParameter size,
        SqliteParameter duration,
        SqliteParameter format,
        SqliteParameter sampleRate)
    {
        var shape = missingIndex < 0 ? -1 : missingIndex % 5;
        size.Value = shape switch
        {
            3 => DBNull.Value,
            4 => AudiobookFile.ProcLinkLstatSize,
            _ => 512_000_000L
        };
        duration.Value = shape == 0 ? DBNull.Value : 36_000.0;
        format.Value = shape == 1 ? DBNull.Value : "m4b";
        sampleRate.Value = shape == 2 ? DBNull.Value : 44_100;
    }

    private static int AudiobookId(int audiobookIndex) => audiobookIndex + 1;

    private static int FileId(int fileIndex) => fileIndex + 1;

    private static bool IsForeignBaseAudiobook(int audiobookIndex) =>
        audiobookIndex % ForeignBaseAudiobookInterval == 1;

    private static string AudiobookBasePath(int audiobookIndex) => AbsolutePath(
        IsForeignBaseAudiobook(audiobookIndex) ? ForeignSyntax : HostSyntax,
        $"book-{audiobookIndex:D5}");

    private static string AbsolutePath(FileSystemPathSyntax syntax, string name) =>
        syntax == FileSystemPathSyntax.Windows
            ? $"C:\\library\\{name}"
            : $"/library/{name}";

    private static IReadOnlyList<string> ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(value => value.GetString()!).ToList();

    private readonly record struct GeneratedFile(
        int Index,
        int AudiobookIndex,
        bool ForeignSyntax,
        bool ForeignBase,
        bool MissingMetadata);
}

/// <summary>Row counts of a generated large library, for test assertions.</summary>
public sealed class LargeLibraryCounts
{
    public int MissingMetadata { get; private set; }

    internal void Record(bool missingMetadata)
    {
        if (missingMetadata)
        {
            MissingMetadata++;
        }
    }
}

/// <summary>A generated large-library database; disposing deletes it.</summary>
public sealed class LargeLibraryFixture : IAsyncDisposable
{
    internal LargeLibraryFixture(string databasePath, DateTime clockUtc)
    {
        DatabasePath = databasePath;
        ClockUtc = clockUtc;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public DateTime ClockUtc { get; }

    public LargeLibraryCounts Counts { get; internal set; } = new();

    public DbContextOptions<ListenArrDbContext> CreateOptions(
        params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(ConnectionString, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
            .AddInterceptors(interceptors)
            .Options;

    public ValueTask DisposeAsync()
    {
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return ValueTask.CompletedTask;
    }
}
