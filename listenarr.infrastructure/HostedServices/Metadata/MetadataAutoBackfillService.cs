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
using Listenarr.Application.Audiobooks.Identifiers;
using Listenarr.Application.Audiobooks.Metadata;
using Listenarr.Application.Common.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Metadata
{
    /// <summary>
    /// Unattended "fill missing from online". Books that hold files but lack
    /// core metadata (no description, cover, publisher, language or date) are
    /// looked up by their own ASIN/ISBN on a slow cadence and the blanks filled.
    /// Blank-only by design: unlike the per-book rescan endpoint, this never
    /// overwrites a populated field, so manual edits survive. Gated by the
    /// MetadataAutoBackfillEnabled setting (off by default).
    /// </summary>
    public class MetadataAutoBackfillService(
        ILogger<MetadataAutoBackfillService> logger,
        IMetadataAutoBackfillProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(15);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "MetadataAutoBackfillService started; checking for books missing metadata every {Minutes} minutes",
                CycleInterval.TotalMinutes);

            await cycleRunner.RunPeriodicAsync(
                nameof(MetadataAutoBackfillService),
                initialDelay: StartupDelay,
                intervalProvider: () => CycleInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("MetadataAutoBackfillService stopped");
        }
    }

    public interface IMetadataAutoBackfillProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public enum MetadataAutoBackfillOutcome
    {
        /// <summary>
        /// Left alone and NOT marked attempted: the book has an unresolved move,
        /// or the record disappeared between candidate selection and apply.
        /// </summary>
        Skipped,
        /// <summary>No ASIN/ISBN could be resolved to provider metadata; marked attempted.</summary>
        NoMetadata,
        /// <summary>Provider answered but every field it knows was already populated.</summary>
        NothingToFill,
        /// <summary>One or more blank fields were filled.</summary>
        Filled
    }

    public class MetadataAutoBackfillProcessor(
        IServiceScopeFactory scopeFactory,
        IAudiobookOperationCoordinator audiobookOperationCoordinator,
        IMoveQueueService moveQueueService,
        ILogger<MetadataAutoBackfillProcessor> logger) : IMetadataAutoBackfillProcessor
    {
        // Provider etiquette: a small batch per cycle with a pause between lookups.
        // 25 every 15 minutes is ~100 books/hour — a few-hundred-book backlog
        // clears in an afternoon without ever bursting at Audible/Audnexus.
        internal const int MaxLookupsPerCycle = 25;
        internal static readonly TimeSpan DelayBetweenLookups = TimeSpan.FromSeconds(3);

        // A book whose lookup found nothing (or still has blanks the provider
        // can't fill) is retried on this cadence rather than every cycle.
        internal static readonly TimeSpan RetryInterval = TimeSpan.FromDays(7);

        private const int MaxAsinLookupAttemptsPerBook = 3;

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetApplicationSettingsAsync();
            if (!settings.MetadataAutoBackfillEnabled)
            {
                logger.LogDebug("Automatic metadata backfill is disabled in settings; skipping cycle");
                return;
            }

            var defaultRegion = string.IsNullOrWhiteSpace(settings.DefaultSearchRegion)
                ? "us"
                : settings.DefaultSearchRegion;

            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var candidates = await repository.GetMetadataBackfillCandidatesAsync(
                MaxLookupsPerCycle,
                DateTime.UtcNow - RetryInterval,
                cancellationToken);

            if (candidates.Count == 0)
            {
                logger.LogDebug("Automatic metadata backfill found no books missing metadata");
                return;
            }

            logger.LogInformation(
                "Automatic metadata backfill: {Count} book(s) missing metadata this cycle",
                candidates.Count);

            var filled = 0;
            var nothingToFill = 0;
            var noMetadata = 0;
            var skipped = 0;
            var failed = 0;

            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[index];

                try
                {
                    var outcome = await ProcessAsync(candidate, defaultRegion, cancellationToken);
                    switch (outcome)
                    {
                        case MetadataAutoBackfillOutcome.Filled: filled++; break;
                        case MetadataAutoBackfillOutcome.NothingToFill: nothingToFill++; break;
                        case MetadataAutoBackfillOutcome.NoMetadata: noMetadata++; break;
                        default: skipped++; break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    failed++;
                    logger.LogWarning(
                        ex,
                        "Automatic metadata backfill failed for audiobook {AudiobookId} ({Title})",
                        candidate.Id,
                        LogRedaction.SanitizeText(candidate.Title));
                }

                if (index < candidates.Count - 1)
                {
                    await Task.Delay(DelayBetweenLookups, cancellationToken);
                }
            }

            logger.LogInformation(
                "Automatic metadata backfill cycle complete: {Filled} filled, {NothingToFill} already complete, {NoMetadata} without provider metadata, {Skipped} skipped, {Failed} failed",
                filled,
                nothingToFill,
                noMetadata,
                skipped,
                failed);
        }

        /// <summary>
        /// One book: resolve provider metadata by its identifiers, then fill blanks
        /// under the per-audiobook exclusive lock so a concurrent edit or move can't
        /// be clobbered.
        /// </summary>
        internal async Task<MetadataAutoBackfillOutcome> ProcessAsync(
            Audiobook candidate,
            string defaultRegion,
            CancellationToken cancellationToken)
        {
            var recovery = await moveQueueService.GetRecoveryStateForAudiobookAsync(candidate.Id, cancellationToken);
            if (recovery.BlocksFilesystemMutation)
            {
                logger.LogDebug(
                    "Skipping automatic metadata backfill for audiobook {AudiobookId}; unresolved move state {Disposition}",
                    candidate.Id,
                    recovery.Disposition);
                return MetadataAutoBackfillOutcome.Skipped;
            }

            using var scope = scopeFactory.CreateScope();
            var metadataService = scope.ServiceProvider.GetRequiredService<IAudiobookMetadataService>();
            var asinLookupService = scope.ServiceProvider.GetService<IAsinLookupService>();
            var imageCacheService = scope.ServiceProvider.GetService<IImageCacheService>();
            var converters = scope.ServiceProvider.GetService<MetadataConverters>()
                ?? new MetadataConverters(
                    imageCacheService,
                    scope.ServiceProvider.GetRequiredService<ILogger<MetadataConverters>>());

            var lookup = await ResolveProviderMetadataAsync(
                candidate,
                defaultRegion,
                metadataService,
                asinLookupService,
                cancellationToken);

            if (lookup == null)
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                await repository.SetMetadataBackfillAttemptedAtAsync(candidate.Id, DateTime.UtcNow, cancellationToken);
                logger.LogInformation(
                    "Automatic metadata backfill found no provider metadata for audiobook {AudiobookId} ({Title}); will retry in {Days} days",
                    candidate.Id,
                    LogRedaction.SanitizeText(candidate.Title),
                    RetryInterval.TotalDays);
                return MetadataAutoBackfillOutcome.NoMetadata;
            }

            var (envelope, resolvedAsin) = lookup.Value;
            var metadata = converters.ConvertAudibleToMetadata(
                envelope.Metadata,
                resolvedAsin,
                string.IsNullOrWhiteSpace(envelope.Source) ? "Audible" : envelope.Source);

            try
            {
                return await audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    candidate.Id,
                    async token =>
                    {
                        await moveQueueService.EnsureFilesystemMutationAllowedAsync(candidate.Id, token);
                        return await ApplyAsync(candidate.Id, resolvedAsin, metadata, imageCacheService, token);
                    },
                    cancellationToken);
            }
            catch (ApplicationConflictException ex)
            {
                // A move started between the pre-check and the lock; leave the
                // book for a later cycle rather than marking it attempted.
                logger.LogDebug(
                    ex,
                    "Automatic metadata backfill deferred for audiobook {AudiobookId}; filesystem mutation currently blocked",
                    candidate.Id);
                return MetadataAutoBackfillOutcome.Skipped;
            }
        }

        private async Task<(AudiobookMetadataEnvelope Envelope, string Asin)?> ResolveProviderMetadataAsync(
            Audiobook candidate,
            string defaultRegion,
            IAudiobookMetadataService metadataService,
            IAsinLookupService? asinLookupService,
            CancellationToken cancellationToken)
        {
            var identifiers = AudiobookIdentifierMapper.GetEffectiveIdentifiers(candidate);
            var attempts = 0;
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task<(AudiobookMetadataEnvelope, string)?> TryAsinAsync(string? rawAsin, string? preferredRegion)
            {
                if (!AudiobookIdentifierNormalizer.TryNormalize(
                        AudiobookExternalIdentifierType.Asin,
                        rawAsin ?? string.Empty,
                        out var asin,
                        out _)
                    || string.IsNullOrWhiteSpace(asin))
                {
                    return null;
                }

                var region = string.IsNullOrWhiteSpace(preferredRegion) ? defaultRegion : preferredRegion!;
                if (!tried.Add($"{asin}|{region}") || attempts >= MaxAsinLookupAttemptsPerBook)
                {
                    return null;
                }

                attempts++;
                try
                {
                    var envelope = await metadataService.GetMetadataAsync(asin, region, cache: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (envelope?.Metadata == null)
                    {
                        return null;
                    }

                    var resolvedAsin = string.IsNullOrWhiteSpace(envelope.Metadata.Asin) ? asin : envelope.Metadata.Asin!;
                    return (envelope, resolvedAsin);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(
                        ex,
                        "Automatic metadata backfill lookup failed for audiobook {AudiobookId} ASIN {Asin} region {Region}",
                        candidate.Id,
                        asin,
                        region);
                    return null;
                }
            }

            foreach (var identifier in identifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Id))
            {
                var result = await TryAsinAsync(
                    string.IsNullOrWhiteSpace(identifier.ValueNormalized) ? identifier.ValueRaw : identifier.ValueNormalized,
                    identifier.Region);
                if (result != null)
                {
                    return result;
                }
            }

            if (asinLookupService == null)
            {
                return null;
            }

            foreach (var identifier in identifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Isbn)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Id))
            {
                var isbn = string.IsNullOrWhiteSpace(identifier.ValueNormalized) ? identifier.ValueRaw : identifier.ValueNormalized;
                if (string.IsNullOrWhiteSpace(isbn))
                {
                    continue;
                }

                try
                {
                    var (success, asinFromIsbn, _) = await asinLookupService.GetAsinFromIsbnAsync(isbn, cancellationToken);
                    if (!success || string.IsNullOrWhiteSpace(asinFromIsbn))
                    {
                        continue;
                    }

                    var result = await TryAsinAsync(asinFromIsbn, null);
                    if (result != null)
                    {
                        return result;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(
                        ex,
                        "Automatic metadata backfill ISBN→ASIN conversion failed for audiobook {AudiobookId}",
                        candidate.Id);
                }
            }

            return null;
        }

        private async Task<MetadataAutoBackfillOutcome> ApplyAsync(
            int audiobookId,
            string resolvedAsin,
            AudibleBookMetadata metadata,
            IImageCacheService? imageCacheService,
            CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audiobook = await repository.GetByIdAsync(audiobookId);
            if (audiobook == null)
            {
                return MetadataAutoBackfillOutcome.Skipped;
            }

            var now = DateTime.UtcNow;
            var filledFields = new List<string>(AudiobookBlankFieldBackfill.Apply(audiobook, metadata));
            var coverMissing = string.IsNullOrWhiteSpace(audiobook.ImageUrl)
                && !string.IsNullOrWhiteSpace(metadata.ImageUrl);

            audiobook.MetadataBackfillAttemptedAt = now;
            cancellationToken.ThrowIfCancellationRequested();
            if (!await repository.UpdateAsync(audiobook))
            {
                return MetadataAutoBackfillOutcome.Skipped;
            }

            if (coverMissing && imageCacheService != null)
            {
                var publishedImageUrl = await CacheCoverAsync(audiobook, resolvedAsin, metadata.ImageUrl!, imageCacheService);
                if (!string.IsNullOrWhiteSpace(publishedImageUrl)
                    && await repository.TryUpdateImageUrlAsync(
                        audiobook.Id,
                        audiobook.ImageUrl,
                        publishedImageUrl,
                        CancellationToken.None))
                {
                    filledFields.Add(nameof(Audiobook.ImageUrl));
                }
            }

            if (filledFields.Count == 0)
            {
                logger.LogDebug(
                    "Automatic metadata backfill: provider knows nothing new for audiobook {AudiobookId}",
                    audiobookId);
                return MetadataAutoBackfillOutcome.NothingToFill;
            }

            logger.LogInformation(
                "Automatic metadata backfill filled {Fields} for audiobook {AudiobookId} ({Title}) from {Source} ASIN {Asin}",
                string.Join(", ", filledFields),
                audiobookId,
                LogRedaction.SanitizeText(audiobook.Title),
                metadata.Source ?? "Audible",
                resolvedAsin);
            return MetadataAutoBackfillOutcome.Filled;
        }

        private async Task<string?> CacheCoverAsync(
            Audiobook audiobook,
            string resolvedAsin,
            string imageUrl,
            IImageCacheService imageCacheService)
        {
            try
            {
                var libraryImagePath = await imageCacheService.MoveToLibraryStorageAsync(resolvedAsin, imageUrl);
                return string.IsNullOrWhiteSpace(libraryImagePath)
                    ? null
                    : "/" + libraryImagePath.TrimStart('/');
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or HttpRequestException
                or TaskCanceledException
                or InvalidOperationException)
            {
                logger.LogWarning(
                    ex,
                    "Automatic metadata backfill could not cache the cover for audiobook {AudiobookId}",
                    audiobook.Id);
                return null;
            }
        }
    }
}
