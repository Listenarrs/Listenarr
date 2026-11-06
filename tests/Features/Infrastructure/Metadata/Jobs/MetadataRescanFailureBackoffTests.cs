/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs
{
    [Trait("Area", "Metadata")]
    [Trait("Name", "MetadataRescanFailureBackoffTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class MetadataRescanFailureBackoffTests : BaseTests
    {
        [Fact]
        public async Task RunCycleAsync_RepeatedFailure_BacksOffAndWarnsOnce()
        {
            Init();
            var audioPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-failure-backoff"),
                "part-00.m4b",
                "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Princess of Mars")
                .WithBasePath(Path.GetDirectoryName(audioPath)!)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var attempts = 0;
            var moveQueueService = new Mock<IMoveQueueService>();
            moveQueueService.Setup(service => service.GetRecoveryStateForAudiobookAsync(
                    audiobook.Id,
                    It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref attempts))
                .ThrowsAsync(new InvalidOperationException("move state store unavailable"));
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var logger = new CapturingLogger();
            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                moveQueueService.Object,
                logger,
                clock);

            await processor.RunCycleAsync(CancellationToken.None);
            Assert.Equal(1, attempts);

            // Still inside the first retry window.
            clock.Advance(MetadataRescanDeferrals.InitialDelay - TimeSpan.FromMinutes(1));
            await processor.RunCycleAsync(CancellationToken.None);
            Assert.Equal(1, attempts);

            clock.Advance(TimeSpan.FromMinutes(1));
            await processor.RunCycleAsync(CancellationToken.None);
            Assert.Equal(2, attempts);

            // The second window is twice the first.
            clock.Advance(MetadataRescanDeferrals.InitialDelay);
            await processor.RunCycleAsync(CancellationToken.None);
            Assert.Equal(2, attempts);

            clock.Advance(MetadataRescanDeferrals.InitialDelay);
            await processor.RunCycleAsync(CancellationToken.None);
            Assert.Equal(3, attempts);

            var failureEntries = logger.Entries
                .Where(entry => entry.Message.StartsWith("Failed to rescan metadata", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(3, failureEntries.Count);
            Assert.Equal(LogLevel.Warning, failureEntries[0].Level);
            Assert.All(failureEntries.Skip(1), entry => Assert.Equal(LogLevel.Debug, entry.Level));
        }

        private sealed class MutableTimeProvider(DateTimeOffset currentTime) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => currentTime;

            public void Advance(TimeSpan value) => currentTime = currentTime.Add(value);
        }

        private sealed class CapturingLogger : ILogger<MetadataRescanProcessor>
        {
            private readonly List<(LogLevel Level, string Message)> _entries = [];

            public IReadOnlyList<(LogLevel Level, string Message)> Entries
            {
                get
                {
                    lock (_entries)
                    {
                        return _entries.ToList();
                    }
                }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_entries)
                {
                    _entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
