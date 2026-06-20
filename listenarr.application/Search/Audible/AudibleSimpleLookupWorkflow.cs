/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Application.Search.Audible
{
    public sealed class AudibleSimpleLookupWorkflow
    {
        private readonly AudibleService _audibleService;
        private readonly MetadataConverters _metadataConverters;

        public AudibleSimpleLookupWorkflow(AudibleService audibleService, MetadataConverters metadataConverters)
        {
            _audibleService = audibleService;
            _metadataConverters = metadataConverters;
        }

        public async Task<List<MetadataSearchResult>?> TrySearchAsync(
            string? searchType,
            string? isbn,
            string? title,
            string actualQuery,
            string region,
            string? language)
        {
            if (searchType == "ISBN" && !string.IsNullOrEmpty(isbn))
            {
                return await SearchByIsbnAsync(isbn, region, language);
            }

            if (searchType == "TITLE" && !string.IsNullOrEmpty(title))
            {
                return await SearchByTitleAsync(title, region, language);
            }

            if (string.IsNullOrWhiteSpace(searchType) && !string.IsNullOrWhiteSpace(actualQuery))
            {
                return await SearchBooksAsync(actualQuery, region, language);
            }

            return null;
        }

        private async Task<List<MetadataSearchResult>?> SearchByIsbnAsync(string isbn, string region, string? language)
        {
            var response = await _audibleService.SearchByIsbnAsync(isbn, 1, 50, region, language);
            if (response?.Results == null || !response.Results.Any())
            {
                return null;
            }

            var converted = new List<SearchResult>();
            var filtered = response.Results.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(language))
            {
                filtered = filtered.Where(b => !string.IsNullOrWhiteSpace(b.Language) && string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var book in filtered.Where(book => !string.IsNullOrWhiteSpace(book.Asin)))
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
                    Isbn = book.Isbn
                };
                var metadata = _metadataConverters.ConvertAudibleToMetadata(bookResponse, book.Asin!, "Audible");
                var searchResult = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, book.Asin!);
                searchResult.IsEnriched = true;
                searchResult.MetadataSource = "Audible";
                converted.Add(searchResult);
            }

            return converted.Any() ? SearchResultConverters.ToMetadataList(converted) : null;
        }

        private async Task<List<MetadataSearchResult>?> SearchByTitleAsync(string title, string region, string? language)
        {
            var response = await _audibleService.SearchByTitleAsync(title, 1, 50, region, language);
            if (response?.Results == null || !response.Results.Any())
            {
                return null;
            }

            var filtered = response.Results.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(language))
            {
                filtered = filtered.Where(b => string.IsNullOrWhiteSpace(b.Language) || string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
            }

            var converted = await AudibleSearchResultMapper.ConvertToSearchResultsAsync(filtered, _metadataConverters);
            return converted.Any() ? SearchResultConverters.ToMetadataList(converted) : null;
        }

        private async Task<List<MetadataSearchResult>?> SearchBooksAsync(string query, string region, string? language)
        {
            var response = await _audibleService.SearchBooksAsync(query, 1, 50, region, language);
            if (response?.Results == null || !response.Results.Any())
            {
                return null;
            }

            var filtered = response.Results.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(language))
            {
                filtered = filtered.Where(b => string.IsNullOrWhiteSpace(b.Language) || string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
            }

            var converted = await AudibleSearchResultMapper.ConvertToSearchResultsAsync(filtered, _metadataConverters);
            return converted.Any() ? SearchResultConverters.ToMetadataList(converted) : null;
        }
    }
}
