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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Monitoring
{
    public partial class SeriesMonitoringService : ISeriesMonitoringService
    {
        private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            "all",
            "english",
            "spanish",
            "german",
            "hungarian",
            "french",
            "polish",
            "italian",
            "russian"
        };

        private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["all"] = "all",
            ["any"] = "all",
            ["english"] = "english",
            ["en"] = "english",
            ["en-us"] = "english",
            ["en-uk"] = "english",
            ["en-gb"] = "english",
            ["en-ca"] = "english",
            ["en-au"] = "english",
            ["en-in"] = "english",
            ["spanish"] = "spanish",
            ["es"] = "spanish",
            ["spa"] = "spanish",
            ["es-es"] = "spanish",
            ["german"] = "german",
            ["de"] = "german",
            ["deu"] = "german",
            ["ger"] = "german",
            ["de-de"] = "german",
            ["deutsch"] = "german",
            ["hungarian"] = "hungarian",
            ["hu"] = "hungarian",
            ["hun"] = "hungarian",
            ["magyar"] = "hungarian",
            ["french"] = "french",
            ["fr"] = "french",
            ["fra"] = "french",
            ["fre"] = "french",
            ["fr-fr"] = "french",
            ["polish"] = "polish",
            ["pl"] = "polish",
            ["pol"] = "polish",
            ["pl-pl"] = "polish",
            ["italian"] = "italian",
            ["it"] = "italian",
            ["ita"] = "italian",
            ["it-it"] = "italian",
            ["russian"] = "russian",
            ["ru"] = "russian",
            ["rus"] = "russian",
            ["ru-ru"] = "russian"
        };

        private readonly IMonitoredSeriesRepository _series;
        private readonly IAudiobookRepository _audiobooks;
        private readonly ISeriesCatalogService _seriesCatalogService;
        private readonly ILibraryAddService _libraryAddService;
        private readonly ILogger<SeriesMonitoringService> _logger;

        public SeriesMonitoringService(
            IMonitoredSeriesRepository series,
            IAudiobookRepository audiobooks,
            ISeriesCatalogService seriesCatalogService,
            ILibraryAddService libraryAddService,
            ILogger<SeriesMonitoringService> logger)
        {
            _series = series;
            _audiobooks = audiobooks;
            _seriesCatalogService = seriesCatalogService;
            _libraryAddService = libraryAddService;
            _logger = logger;
        }

        public async Task<MonitoredSeries?> GetMonitoredSeriesAsync(
            string name,
            string region,
            string language,
            CancellationToken cancellationToken = default)
        {
            var normalizedName = NormalizeSeriesName(name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            var normalizedRegion = NormalizeRegion(region);
            var normalizedLanguage = NormalizeLanguage(language, fallbackToEnglish: true);

            return await _series.GetByNameRegionLanguageAsync(normalizedName, normalizedRegion, normalizedLanguage, cancellationToken);
        }

        public async Task<MonitorSeriesOperationResult> MonitorSeriesAsync(
            MonitorSeriesRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var normalizedName = NormalizeSeriesName(request.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                throw new ArgumentException("Series name is required.", nameof(request));
            }

            var normalizedRegion = NormalizeRegion(request.Region);
            var normalizedLanguage = NormalizeLanguage(request.Language, fallbackToEnglish: true);
            var displayName = request.Name.Trim();

            var monitoredSeries = await _series.GetByNameRegionLanguageAsync(normalizedName, normalizedRegion, normalizedLanguage, cancellationToken);

            if (monitoredSeries == null)
            {
                monitoredSeries = new MonitoredSeries
                {
                    SeriesName = displayName,
                    SeriesNameNormalized = normalizedName,
                    SeriesAsin = NormalizeOptionalIdentifier(request.Asin),
                    Region = normalizedRegion,
                    Language = normalizedLanguage,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
            }
            else
            {
                monitoredSeries.SeriesName = displayName;
                monitoredSeries.SeriesAsin = NormalizeOptionalIdentifier(request.Asin) ?? monitoredSeries.SeriesAsin;
                monitoredSeries.UpdatedAt = DateTime.UtcNow;
            }

            monitoredSeries = await _series.UpsertAsync(monitoredSeries, cancellationToken);

            var syncResult = await SyncSeriesInternalAsync(monitoredSeries, cancellationToken);
            return new MonitorSeriesOperationResult
            {
                MonitoredSeries = monitoredSeries,
                SyncResult = syncResult
            };
        }

        public async Task<bool> UnmonitorSeriesAsync(int id, CancellationToken cancellationToken = default)
        {
            return await _series.DeleteAsync(id, cancellationToken);
        }

        public async Task<MonitorSeriesSyncResult> SyncSeriesAsync(int id, CancellationToken cancellationToken = default)
        {
            var monitoredSeries = await _series.GetByIdAsync(id, cancellationToken);

            if (monitoredSeries == null)
            {
                return new MonitorSeriesSyncResult
                {
                    ErrorMessage = "Monitored series not found.",
                    FailedCount = 1,
                    Succeeded = false
                };
            }

            return await SyncSeriesInternalAsync(monitoredSeries, cancellationToken);
        }

        public async Task<int> SyncDueSeriesAsync(CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            var dueSeries = await _series.GetDueForSyncAsync(cutoff, cancellationToken);

            var syncedCount = 0;
            foreach (var monitoredSeries in dueSeries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await SyncSeriesInternalAsync(monitoredSeries, cancellationToken);
                if (result.Succeeded)
                {
                    syncedCount++;
                }
            }

            return syncedCount;
        }

        private async Task<MonitorSeriesSyncResult> SyncSeriesInternalAsync(
            MonitoredSeries monitoredSeries,
            CancellationToken cancellationToken)
        {
            var result = new MonitorSeriesSyncResult();

            try
            {
                var catalog = await _seriesCatalogService.GetCatalogAsync(
                    monitoredSeries.SeriesName,
                    monitoredSeries.Region,
                    limit: 500,
                    language: null,
                    forceRefresh: true,
                    cancellationToken: cancellationToken);

                if (catalog == null)
                {
                    result.Succeeded = false;
                    result.ErrorMessage = "Series catalog could not be loaded.";
                    monitoredSeries.LastError = TruncateError(result.ErrorMessage);
                    monitoredSeries.LastCheckedAt = DateTime.UtcNow;
                    monitoredSeries.UpdatedAt = DateTime.UtcNow;
                    await _series.UpsertAsync(monitoredSeries, cancellationToken);
                    return result;
                }

                monitoredSeries.SeriesAsin = NormalizeOptionalIdentifier(catalog.Series.Asin) ?? monitoredSeries.SeriesAsin;

                var existingLibrary = await _audiobooks.GetAllAsync();

                foreach (var book in catalog.Books)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ShouldIncludeBookForLanguage(book, monitoredSeries.Language))
                    {
                        continue;
                    }

                    if (FindExistingLibraryMatch(book, existingLibrary) != null)
                    {
                        result.ExistingCount++;
                        continue;
                    }

                    var addResult = await _libraryAddService.AddToLibraryAsync(
                        new LibraryAddOperationRequest
                        {
                            Metadata = MapToMetadata(book),
                            Monitored = true,
                            AutoSearch = false,
                            HistorySource = "SeriesMonitoring",
                            HistoryMessage =
                                $"Audiobook '{book.Title}' added automatically from monitored series '{monitoredSeries.SeriesName}'"
                        },
                        cancellationToken);

                    if (addResult.Added && addResult.Audiobook != null)
                    {
                        result.AddedCount++;
                        existingLibrary.Add(addResult.Audiobook);
                        continue;
                    }

                    if (addResult.AlreadyExists)
                    {
                        result.ExistingCount++;
                        if (addResult.Audiobook != null)
                        {
                            existingLibrary.Add(addResult.Audiobook);
                        }
                        continue;
                    }

                    result.FailedCount++;
                }

                monitoredSeries.LastCheckedAt = DateTime.UtcNow;
                monitoredSeries.LastSuccessfulSyncAt = monitoredSeries.LastCheckedAt;
                monitoredSeries.LastError = null;
                monitoredSeries.UpdatedAt = DateTime.UtcNow;
                await _series.UpsertAsync(monitoredSeries, cancellationToken);

                result.Succeeded = true;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(
                    ex,
                    "Failed to sync monitored series '{Series}' ({Region}/{Language})",
                    monitoredSeries.SeriesName,
                    monitoredSeries.Region,
                    monitoredSeries.Language);

                result.Succeeded = false;
                result.ErrorMessage = ex.Message;
                result.FailedCount++;
                monitoredSeries.LastCheckedAt = DateTime.UtcNow;
                monitoredSeries.LastError = TruncateError(ex.Message);
                monitoredSeries.UpdatedAt = DateTime.UtcNow;
                await _series.UpsertAsync(monitoredSeries, cancellationToken);
                return result;
            }
        }

    }
}
