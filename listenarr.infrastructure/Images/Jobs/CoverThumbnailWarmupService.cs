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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Jobs
{
    public interface ICoverThumbnailWarmupProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Pre-generates grid cover thumbnails in the background so the library grid never has to build
    /// them on-demand while the user scrolls. Runs shortly after startup and periodically to pick up
    /// newly added covers. Idempotent: existing fresh thumbnails are skipped by the thumbnail service.
    /// </summary>
    public class CoverThumbnailWarmupService(
        ILogger<CoverThumbnailWarmupService> logger,
        ICoverThumbnailWarmupProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Cover Thumbnail Warmup Service is starting");
            await cycleRunner.RunPeriodicAsync(
                nameof(CoverThumbnailWarmupService),
                initialDelay: InitialDelay,
                intervalProvider: () => Interval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);
        }
    }

    public class CoverThumbnailWarmupProcessor(
        IServiceScopeFactory scopeFactory,
        ICoverThumbnailService thumbnailService,
        IImageCacheService imageCacheService,
        IApplicationPathService applicationPathService,
        ILogger<CoverThumbnailWarmupProcessor> logger) : ICoverThumbnailWarmupProcessor
    {
        // Cap concurrent ImageSharp decodes of full-size originals to keep peak memory bounded.
        private const int MaxConcurrency = 3;
        private const string WarmSize = "grid";

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();

            var books = await repository.GetLibraryAsync();
            var contentRoot = applicationPathService.ContentRootPath;
            logger.LogInformation("Cover thumbnail warmup starting for {Count} library items.", books.Count);

            using var gate = new SemaphoreSlim(MaxConcurrency);
            var warmed = 0;
            var tasks = new List<Task>(books.Count);

            foreach (var book in books)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identifier = ResolveCoverIdentifier(book);
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    continue;
                }

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var relative = await imageCacheService.GetCachedImagePathAsync(identifier).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(relative))
                        {
                            return;
                        }

                        var full = Path.GetFullPath(Path.Combine(
                            contentRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!File.Exists(full))
                        {
                            return;
                        }

                        var thumb = await thumbnailService
                            .GetOrCreateThumbnailAsync(full, WarmSize, cancellationToken).ConfigureAwait(false);
                        if (thumb != null)
                        {
                            Interlocked.Increment(ref warmed);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Cover thumbnail warmup failed for {Identifier}", identifier);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            logger.LogInformation("Cover thumbnail warmup complete: {Warmed} thumbnails ready.", warmed);
        }

        // Book covers are stored as /api/.../images/{id}; fall back to the ASIN.
        private static string? ResolveCoverIdentifier(Audiobook book)
        {
            var url = book.ImageUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                const string marker = "/images/";
                var idx = url.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var id = url[(idx + marker.Length)..];
                    var query = id.IndexOf('?');
                    if (query >= 0)
                    {
                        id = id[..query];
                    }
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        return id;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(book.Asin) ? null : book.Asin;
        }
    }
}
