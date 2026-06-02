/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Audible
{
    public sealed class AudibleAuthorSearchWorkflow
    {
        private readonly AudibleService _audibleService;
        private readonly AudibleAuthorPageCollector _authorPageCollector;
        private readonly MetadataConverters _metadataConverters;
        private readonly ILogger<AudibleAuthorSearchWorkflow> _logger;

        public AudibleAuthorSearchWorkflow(
            AudibleService audibleService,
            AudibleAuthorPageCollector authorPageCollector,
            MetadataConverters metadataConverters,
            ILogger<AudibleAuthorSearchWorkflow> logger)
        {
            _audibleService = audibleService;
            _authorPageCollector = authorPageCollector;
            _metadataConverters = metadataConverters;
            _logger = logger;
        }

        public async Task<List<MetadataSearchResult>?> TrySearchAsync(
            string? searchType,
            string? author,
            string? title,
            string? isbn,
            int candidateLimit,
            string region,
            string? language)
        {
            if (searchType == "AUTHOR" && !string.IsNullOrEmpty(author))
            {
                return await SearchByAuthorAsync(author, candidateLimit, region, language);
            }

            if (searchType == "AUTHOR_TITLE" && !string.IsNullOrEmpty(author))
            {
                return await SearchByAuthorAndTitleAsync(author, title, isbn, candidateLimit, region, language);
            }

            return null;
        }

        private async Task<List<MetadataSearchResult>?> SearchByAuthorAsync(
            string author,
            int candidateLimit,
            string region,
            string? language)
        {
            var aggregated = await _authorPageCollector.CollectAsync(
                author,
                candidateLimit,
                region,
                language,
                "author");

            if (!aggregated.Any())
            {
                return null;
            }

            var deduplicated = DeduplicateByAsin(aggregated);
            _logger.LogInformation(
                "Deduplicated author results for '{Author}': {OriginalCount} -> {DeduplicatedCount}",
                author,
                aggregated.Count,
                deduplicated.Count);

            var converted = new List<SearchResult>();
            var authorFiltered = ApplyStrictLanguageFilter(deduplicated, language);
            foreach (var book in authorFiltered.Where(book => !string.IsNullOrWhiteSpace(book.Asin)))
            {
                var bookResponse = new AudibleBookResponse
                {
                    Asin = book.Asin,
                    Title = book.Title,
                    Subtitle = book.Subtitle,
                    Authors = book.Authors,
                    ImageUrl = book.ImageUrl,
                    Language = book.Language,
                    BookFormat = book.BookFormat,
                    Genres = book.Genres,
                    Series = book.Series,
                    Publisher = book.Publisher,
                    Narrators = book.Narrators,
                    ReleaseDate = book.ReleaseDate,
                    Region = region
                };
                var metadata = _metadataConverters.ConvertAudibleToMetadata(bookResponse, book.Asin!, "Audible");
                var searchResult = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, book.Asin!);
                searchResult.IsEnriched = true;
                searchResult.MetadataSource = "Audible";
                converted.Add(searchResult);
            }

            return converted.Any() ? SearchResultConverters.ToMetadataList(converted) : null;
        }

        private async Task<List<MetadataSearchResult>?> SearchByAuthorAndTitleAsync(
            string author,
            string? title,
            string? isbn,
            int candidateLimit,
            string region,
            string? language)
        {
            try { _logger.LogInformation("Entering AUTHOR_TITLE branch: author='{Author}', title='{Title}', isbn='{Isbn}'", author, title, isbn); }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            var aggregated = await _authorPageCollector.CollectAsync(
                author,
                candidateLimit,
                region,
                language,
                "AUTHOR_TITLE");

            if (aggregated?.Any() != true)
            {
                return null;
            }

            var deduplicated = DeduplicateByAsin(aggregated);
            _logger.LogInformation(
                "Deduplicated AUTHOR_TITLE results for '{Author}': {OriginalCount} -> {DeduplicatedCount}",
                author,
                aggregated.Count,
                deduplicated.Count);

            try { _logger.LogInformation("Audible author lookup returned {Count} aggregated results for author '{Author}'", deduplicated.Count, author); }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            var authorFiltered = ApplyStrictLanguageFilter(deduplicated, language);

            if (!string.IsNullOrEmpty(title))
            {
                authorFiltered = authorFiltered.Where(b =>
                    (!string.IsNullOrWhiteSpace(b.Title) && b.Title.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(b.Subtitle) && b.Subtitle.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var detailedMetaByAsin = new Dictionary<string, AudibleBookResponse>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(isbn))
            {
                authorFiltered = await FilterByIsbnAsync(aggregated, authorFiltered, isbn, candidateLimit, region, language, detailedMetaByAsin);
            }

            try { _logger.LogInformation("[DBG] authorFiltered count after language/title/isbn filtering: {Count}", authorFiltered.Count()); }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            var converted = await AudibleSearchResultMapper.ConvertToSearchResultsAsync(
                authorFiltered,
                _metadataConverters,
                region,
                detailedMetaByAsin,
                _logger,
                continueOnConversionError: true);

            return converted.Any() ? SearchResultConverters.ToMetadataList(converted) : null;
        }

        private async Task<IEnumerable<AudibleSearchResult>> FilterByIsbnAsync(
            IReadOnlyCollection<AudibleSearchResult> aggregated,
            IEnumerable<AudibleSearchResult> authorFiltered,
            string isbn,
            int candidateLimit,
            string region,
            string? language,
            IDictionary<string, AudibleBookResponse> detailedMetaByAsin)
        {
            var isbnScanLimit = Math.Min(200, Math.Max(50, candidateLimit));
            var scanCandidates = aggregated.Where(r => !string.IsNullOrWhiteSpace(r.Asin)).Take(isbnScanLimit).ToList();
            try { _logger.LogInformation("Scanning up to {Limit} author candidates for ISBN {Isbn}", scanCandidates.Count, isbn); }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            foreach (var candidate in scanCandidates.Where(c => !string.IsNullOrWhiteSpace(c.Asin)))
            {
                try
                {
                    var metadata = await _audibleService.GetBookMetadataAsync(candidate.Asin!, region, true, language);
                    if (metadata == null)
                    {
                        continue;
                    }

                    detailedMetaByAsin[candidate.Asin!] = metadata;
                    if (!string.IsNullOrWhiteSpace(metadata.Isbn) && string.Equals(metadata.Isbn.Trim(), isbn, StringComparison.OrdinalIgnoreCase))
                    {
                        return authorFiltered.Where(r => !string.IsNullOrWhiteSpace(r.Asin) && string.Equals(r.Asin, candidate.Asin, StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch (Exception exMeta) when (exMeta is not OperationCanceledException && exMeta is not OutOfMemoryException && exMeta is not StackOverflowException)
                {
                    _logger.LogDebug(exMeta, "Failed fetching audible metadata for ASIN {Asin} while scanning for ISBN", candidate.Asin);
                }
            }

            return authorFiltered;
        }

        private static List<AudibleSearchResult> DeduplicateByAsin(IEnumerable<AudibleSearchResult> books)
        {
            return books
                .Where(b => !string.IsNullOrWhiteSpace(b.Asin))
                .GroupBy(b => b.Asin, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static IEnumerable<AudibleSearchResult> ApplyStrictLanguageFilter(
            IEnumerable<AudibleSearchResult> books,
            string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return books;
            }

            return books.Where(b => !string.IsNullOrWhiteSpace(b.Language) && string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
        }
    }
}
