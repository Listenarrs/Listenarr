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
    public sealed class AudibleAuthorPageCollector
    {
        private readonly AudibleService _audibleService;
        private readonly ILogger<AudibleAuthorPageCollector> _logger;

        public AudibleAuthorPageCollector(AudibleService audibleService, ILogger<AudibleAuthorPageCollector> logger)
        {
            _audibleService = audibleService;
            _logger = logger;
        }

        public async Task<List<AudibleSearchResult>> CollectAsync(
            string author,
            int candidateLimit,
            string region,
            string? language,
            string logContext)
        {
            var aggregated = new List<AudibleSearchResult>();
            var pageSize = Math.Min(50, Math.Max(10, candidateLimit));

            for (var page = 1; page <= int.MaxValue; page++)
            {
                try
                {
                    var pageRes = await _audibleService.SearchByAuthorAsync(author, page, pageSize, region, language);
                    var pageCount = pageRes?.Results?.Count ?? 0;
                    aggregated.AddRange(pageRes?.Results ?? Enumerable.Empty<AudibleSearchResult>());

                    _logger.LogInformation(
                        "Audible {Context}: page {Page} returned {PageCount} results (aggregated {AggregatedCount}) for author '{Author}'",
                        logContext,
                        page,
                        pageCount,
                        aggregated.Count,
                        author);

                    if (pageRes?.Results == null || pageCount == 0)
                    {
                        _logger.LogInformation("Audible {Context}: stopping aggregation - page {Page} returned no results", logContext, page);
                        break;
                    }

                    if (pageCount < pageSize)
                    {
                        _logger.LogInformation("Audible {Context}: stopping aggregation - page {Page} count {PageCount} < pageSize {PageSize}", logContext, page, pageCount, pageSize);
                        break;
                    }
                }
                catch (Exception exPage) when (exPage is not OperationCanceledException && exPage is not OutOfMemoryException && exPage is not StackOverflowException)
                {
                    _logger.LogDebug(exPage, "Failed fetching audible author page {Page} for author {Author}", page, author);
                    break;
                }
            }

            _logger.LogInformation(
                "Audible {Context}: finished aggregating pages for '{Author}': aggregated={AggregatedCount}, candidateLimit={CandidateLimit}, pageSize={PageSize}",
                logContext,
                author,
                aggregated.Count,
                candidateLimit,
                pageSize);

            return aggregated;
        }
    }
}
