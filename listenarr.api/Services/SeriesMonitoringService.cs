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
using System.Globalization;
using System.Text;

namespace Listenarr.Api.Services
{
    public class SeriesMonitoringService : ISeriesMonitoringService
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

        private static AudibleBookMetadata MapToMetadata(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new AudibleBookMetadata
            {
                Asin = book.Asin,
                Title = book.Title,
                Subtitle = book.Subtitle,
                Authors = (book.Authors ?? new List<AudibleAuthor>())
                    .Select(author => author.Name)
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Cast<string>()
                    .ToList(),
                ImageUrl = book.ImageUrl,
                Runtime = runtime,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = (book.Narrators ?? new List<AudibleNarrator>())
                    .Select(narrator => narrator.Name)
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                    .Cast<string>()
                    .ToList(),
                Genres = (book.Genres ?? new List<AudibleGenre>())
                    .Select(genre => genre.Name)
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Cast<string>()
                    .ToList(),
                Series = primarySeries?.Name,
                SeriesNumber = primarySeries?.Position,
                PublishedDate = book.ReleaseDate,
                PublishYear = TryExtractPublishYear(book.ReleaseDate),
                Isbn = string.IsNullOrWhiteSpace(book.Isbn) ? new List<string>() : new List<string> { book.Isbn },
                Source = "Audible"
            };
        }

        private static Audiobook? FindExistingLibraryMatch(
            AudibleSearchResult book,
            IEnumerable<Audiobook> libraryBooks)
        {
            var asin = NormalizeIdentifier(book.Asin);
            if (!string.IsNullOrWhiteSpace(asin))
            {
                var asinMatch = libraryBooks.FirstOrDefault(candidate =>
                    NormalizeIdentifier(candidate.Asin) == asin);
                if (asinMatch != null)
                {
                    return asinMatch;
                }
            }

            var isbn = NormalizeIdentifier(book.Isbn);
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                var isbnMatch = libraryBooks.FirstOrDefault(candidate =>
                    (candidate.Isbn ?? new List<string>()).Any(value => NormalizeIdentifier(value) == isbn));
                if (isbnMatch != null)
                {
                    return isbnMatch;
                }
            }

            var titleAuthorKey = BuildTitleAuthorKey(
                book.Title,
                (book.Authors ?? new List<AudibleAuthor>())
                    .Select(author => author.Name)
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Cast<string>()
                    .ToList());

            if (string.IsNullOrWhiteSpace(titleAuthorKey))
            {
                return null;
            }

            return libraryBooks.FirstOrDefault(candidate =>
                BuildTitleAuthorKey(candidate.Title, candidate.Authors) == titleAuthorKey);
        }

        private static bool ShouldIncludeBookForLanguage(AudibleSearchResult book, string preferredLanguage)
        {
            if (string.Equals(preferredLanguage, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedBookLanguage = NormalizeLanguage(book.Language, fallbackToEnglish: false);
            return string.Equals(normalizedBookLanguage, preferredLanguage, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSeriesName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
                else if (char.IsWhiteSpace(character))
                {
                    builder.Append(' ');
                }
            }

            return string.Join(
                ' ',
                builder.ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string NormalizeRegion(string? region)
        {
            return AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
        }

        private static string NormalizeLanguage(string? language, bool fallbackToEnglish)
        {
            var normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return fallbackToEnglish ? "english" : string.Empty;
            }

            if (LanguageAliases.TryGetValue(normalized, out var alias))
            {
                return alias;
            }

            if (SupportedLanguages.Contains(normalized))
            {
                return normalized;
            }

            return fallbackToEnglish ? "english" : string.Empty;
        }

        private static string? NormalizeOptionalIdentifier(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new string(value.Trim().Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        private static string NormalizeIdentifier(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        private static string BuildTitleAuthorKey(string? title, IEnumerable<string>? authors)
        {
            var normalizedTitle = NormalizeSeriesName(title);
            var normalizedAuthors = string.Join(
                "|",
                (authors ?? Enumerable.Empty<string>())
                    .Select(NormalizeSeriesName)
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .OrderBy(author => author));

            return string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedAuthors)
                ? string.Empty
                : $"{normalizedTitle}::{normalizedAuthors}";
        }

        private static string? TryExtractPublishYear(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(value, "\\d{4}");
            return match.Success ? match.Value : null;
        }

        private static string? TruncateError(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.Length <= 2048 ? value : value[..2048];
        }
    }
}
