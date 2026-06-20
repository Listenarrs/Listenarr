/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using AsyncKeyedLock;
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Jobs
{
    // Background hosted service to rescan files missing metadata and populate DB fields
    public class MetadataRescanService(
        ILogger<MetadataRescanService> logger,
        IMetadataRescanProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("MetadataRescanService starting");
            await cycleRunner.RunPeriodicAsync(
                nameof(MetadataRescanService),
                initialDelay: null,
                intervalProvider: () => Interval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);
            logger.LogInformation("MetadataRescanService stopping");
        }
    }

    public class MetadataRescanProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<MetadataRescanProcessor> logger) : IMetadataRescanProcessor
    {
        private readonly AsyncNonKeyedLocker _sem = new(2); // bound concurrent extractions

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var candidates = await fileRepository.GetMissingMetadataAsync(20, cancellationToken);

            if (candidates.Any())
            {
                logger.LogInformation("Found {Count} files missing metadata to rescan", candidates.Count);
            }

            var tasks = new List<Task>();
            foreach (var candidate in candidates.Select(f => new { f.Id, f.Path }))
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var releaser = await _sem.LockAsync(cancellationToken);
                    try
                    {
                        using var taskScope = scopeFactory.CreateScope();
                        var taskFileRepository = taskScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                        var taskAudiobookRepository = taskScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                        var taskMetadataService = taskScope.ServiceProvider.GetRequiredService<IMetadataService>();

                        var file = await taskFileRepository.GetByIdAsync(candidate.Id, cancellationToken);
                        if (file == null)
                        {
                            logger.LogDebug("Skipping metadata rescan for missing file id={Id}", candidate.Id);
                            return;
                        }

                        if (!FileUtils.IsAudioFile(file.Path ?? string.Empty))
                        {
                            var audiobook = await taskAudiobookRepository.GetByIdAsync(file.AudiobookId);
                            if (audiobook != null && string.Equals(audiobook.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))
                            {
                                audiobook.FilePath = null;
                                audiobook.FileSize = null;
                                await taskAudiobookRepository.UpdateAsync(audiobook);
                            }

                            await taskFileRepository.DeleteAsync(file.Id, cancellationToken);
                            logger.LogInformation("Removed non-audio AudiobookFile entry id={Id} path={Path}", file.Id, LogRedaction.SanitizeFilePath(file.Path));
                            return;
                        }

                        logger.LogInformation("Re-extracting metadata for file id={Id} path={Path}", file.Id, LogRedaction.SanitizeFilePath(file.Path));

                        if (cancellationToken.IsCancellationRequested)
                        {
                            logger.LogDebug("Cancellation requested before extracting metadata for file id={Id}", file.Id);
                            return;
                        }

                        var meta = await taskMetadataService.ExtractFileMetadataAsync(file.Path ?? string.Empty);
                        if (meta != null)
                        {
                            var fi = new System.IO.FileInfo(file.Path ?? string.Empty);
                            file.Size = fi.Exists ? fi.Length : file.Size;
                            file.DurationSeconds = Math.Abs(meta.Duration.TotalSeconds) > double.Epsilon ? meta.Duration.TotalSeconds : file.DurationSeconds;
                            file.Format = !string.IsNullOrEmpty(meta.Format) ? meta.Format : file.Format;
                            file.Bitrate = meta.BitRate != 0 ? meta.BitRate : file.Bitrate;
                            file.SampleRate = meta.SampleRate != 0 ? meta.SampleRate : file.SampleRate;
                            file.Channels = meta.Channels != 0 ? meta.Channels : file.Channels;

                            await taskFileRepository.UpdateAsync(file, cancellationToken);
                            logger.LogInformation("Updated metadata for file id={Id}", file.Id);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogDebug("Metadata rescan cancelled for file id={Id}", candidate.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogWarning(ex, "Failed to rescan metadata for file id={Id} path={Path}", candidate.Id, LogRedaction.SanitizeFilePath(candidate.Path));
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
    }
}
