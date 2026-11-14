/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Collections.Concurrent;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Features.Infrastructure.Persistence.LargeLibrary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs;

public sealed partial class MetadataRescanSchedulingTests
{
    private static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(5);

    // An hour ahead of the wall clock, so rows created with the wall-clock default are due.
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow.AddHours(1));
    private readonly ConcurrentQueue<string> _cycleExtractions = new();
    private readonly List<ServiceProvider> _stores = [];
    private IDbContextFactory<ListenArrDbContext> _factory = null!;
    private ServiceProvider _store = null!;
    private int _seededBooks;

    // Decides each extraction by the file's public path; the default returns complete metadata.
    private Func<string, Task<AudioMetadata?>> _extract = _ => Task.FromResult<AudioMetadata?>(CompleteMetadata());

    // The stores' databases live in the test's temporary directory, which the base class removes.
    public override async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static AudioMetadata CompleteMetadata() => new()
    {
        Duration = TimeSpan.FromSeconds(90),
        Format = "m4b",
        SampleRate = 44100
    };

    // A fresh file-backed SQLite database behind the whole service graph, with the clock and
    // a scripted extractor. Calling it again starts a second, independent store.
    private async Task InitSqliteAsync(Action<ServiceCollectionBuilder>? configure = null)
    {
        var databasePath = Path.Join(FileService.GetTempPath(), $"rescan-scheduling-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var factory = new SqliteContextFactory(options);
        _factory = factory;
        var extractor = new Mock<IMetadataService>();
        extractor.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .Returns((MetadataFileSource source) =>
            {
                _cycleExtractions.Enqueue(source.PublicPath);
                return _extract(source.PublicPath);
            });
        var builder = new ServiceCollectionBuilder()
            .WithContentRootPath(FileService.GetTempPath())
            .WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext()))
            .WithSingleton(extractor.Object)
            .WithSingleton<TimeProvider>(_clock);
        configure?.Invoke(builder);
        _store = builder.Build().BuildServiceProvider();
        _stores.Add(_store);
        await using var setup = await factory.CreateDbContextAsync();
        await setup.Database.EnsureCreatedAsync();
    }

    private MetadataRescanProcessor NewProcessor(
        IMoveQueueService? moveQueueService = null,
        ILogger<MetadataRescanProcessor>? logger = null) => new(
        _store.GetRequiredService<IServiceScopeFactory>(),
        _store.GetRequiredService<IAudiobookOperationCoordinator>(),
        moveQueueService ?? _store.GetRequiredService<IMoveQueueService>(),
        logger ?? NullLogger<MetadataRescanProcessor>.Instance);

    // One audiobook (a corpus title) with real files on disk. A due time, when given, replaces
    // the creation-time default, as a file arriving at that clock time would have.
    private async Task<SeededBook> SeedBookAsync(string folder, int fileCount, DateTime? dueUtc = null)
    {
        var books = LargeLibraryFixtureBuilder.LoadBooks();
        var corpusBook = books[_seededBooks++ % books.Count];
        var basePath = FileService.GetTempDirectory($"rescan-scheduling-{folder}");
        var audiobook = await _store.GetRequiredService<IAudiobookRepository>().AddAsync(new AudiobookBuilder()
            .WithTitle(corpusBook.Title)
            .WithBasePath(basePath)
            .Build());
        var files = new List<(int Id, string Path)>();
        for (var index = 0; index < fileCount; index++)
        {
            var path = await FileService.GetFileAsync(basePath, $"part-{index:D3}.m4b", "audio");
            var pending = new AudiobookFileBuilder().WithAudiobook(audiobook).WithPath(path).Build();
            if (dueUtc.HasValue)
            {
                pending.MetadataRescanDueUtc = dueUtc.Value;
            }

            var file = await _store.GetRequiredService<IAudiobookFileRepository>().AddAsync(pending);
            files.Add((file.Id, path));
        }

        return new SeededBook(audiobook.Id, basePath, files);
    }

    // Runs one cycle at the current clock time, then moves the clock to the next cycle.
    private async Task<IReadOnlyList<string>> RunCycleAsync(
        MetadataRescanProcessor processor,
        CancellationToken cancellationToken = default)
    {
        _cycleExtractions.Clear();
        await processor.RunCycleAsync(cancellationToken);
        _clock.Advance(CycleInterval);
        return [.. _cycleExtractions];
    }

    private async Task<AudiobookFile> ReadFileAsync(int fileId)
    {
        await using var context = await _factory.CreateDbContextAsync();
        return await context.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
    }

    private DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

    private sealed record SeededBook(int AudiobookId, string BasePath, IReadOnlyList<(int Id, string Path)> Files)
    {
        public IReadOnlyList<string> Paths => Files.Select(file => file.Path).ToList();
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _start = start;
        private DateTimeOffset _now = start;

        public DateTime Start => _start.UtcDateTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public void Reset() => _now = _start;
    }

    private sealed class SqliteContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class CapturingLogger : ILogger<MetadataRescanProcessor>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<LogLevel> LevelsContaining(string text) =>
            _entries.Where(entry => entry.Message.Contains(text, StringComparison.Ordinal))
                .Select(entry => entry.Level)
                .ToList();

        public void Clear() => _entries.Clear();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
