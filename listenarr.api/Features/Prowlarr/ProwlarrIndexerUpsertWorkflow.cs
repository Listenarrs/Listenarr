/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Api.Features.Prowlarr
{
    public sealed class ProwlarrIndexerUpsertWorkflow
    {
        private readonly IIndexerRepository _indexerRepository;
        private readonly ILogger<ProwlarrIndexerUpsertWorkflow> _logger;

        public ProwlarrIndexerUpsertWorkflow(
            IIndexerRepository indexerRepository,
            ILogger<ProwlarrIndexerUpsertWorkflow> logger)
        {
            _indexerRepository = indexerRepository;
            _logger = logger;
        }

        public async Task<ProwlarrIndexerUpsertResult> UpsertFromPutAsync(int id, ParsedProwlarrIndexerPayload parsed)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            var created = false;

            if (indexer == null)
            {
                indexer = await FindExistingAsync(parsed.Url, parsed.ApiKey);
                if (indexer == null)
                {
                    indexer = CreateIndexer(parsed);
                    indexer = await _indexerRepository.AddAsync(indexer);
                    created = true;

                    _logger.LogInformation(
                        "Prowlarr: Created indexer (upsert from PUT) (name={Name}, url={Url}, apiKeyPresent={HasApiKey})",
                        indexer.Name,
                        indexer.Url,
                        !string.IsNullOrEmpty(indexer.ApiKey));
                }
            }

            ApplyParsedValues(indexer, parsed);
            await _indexerRepository.UpdateAsync(indexer);
            await CleanupDuplicateIndexersAsync(NormalizeIndexerUrl(indexer.Url), indexer.ApiKey ?? string.Empty);

            var stillExists = (await _indexerRepository.GetByIdAsync(indexer.Id)) != null;
            return new ProwlarrIndexerUpsertResult(indexer, created, stillExists);
        }

        public async Task<ProwlarrIndexerBulkImportResult> ImportManyAsync(IEnumerable<ParsedProwlarrIndexerPayload> payloads)
        {
            var created = 0;
            var skipped = 0;
            var createdIndexers = new List<Indexer>();
            var existingIndexers = (await _indexerRepository.GetAllAsync()).ToList();

            foreach (var parsed in payloads)
            {
                var normalizedUrl = NormalizeIndexerUrl(parsed.Url);
                var exists = existingIndexers.FirstOrDefault(i => NormalizeIndexerUrl(i.Url) == normalizedUrl && (i.ApiKey ?? string.Empty) == (parsed.ApiKey ?? string.Empty));
                if (exists != null)
                {
                    skipped++;
                    _logger.LogInformation("Prowlarr: Skipping existing indexer (name={Name}, url={Url}, apiKeyPresent={HasApiKey})", parsed.Name, exists.Url, !string.IsNullOrEmpty(parsed.ApiKey));
                    continue;
                }

                var indexer = CreateIndexer(parsed);
                indexer = await _indexerRepository.AddAsync(indexer);
                existingIndexers.Add(indexer);
                created++;
                createdIndexers.Add(indexer);

                _logger.LogInformation("Prowlarr: Created indexer (name={Name}, url={Url}, apiKeyPresent={HasApiKey})", indexer.Name, indexer.Url, !string.IsNullOrEmpty(indexer.ApiKey));
            }

            foreach (var createdIndexer in createdIndexers.ToList())
            {
                await CleanupDuplicateIndexersAsync(NormalizeIndexerUrl(createdIndexer.Url), createdIndexer.ApiKey ?? string.Empty);
            }

            return new ProwlarrIndexerBulkImportResult(created, skipped, createdIndexers);
        }

        private async Task<Indexer?> FindExistingAsync(string url, string? apiKey)
        {
            var normalized = NormalizeIndexerUrl(url);
            var allIndexers = await _indexerRepository.GetAllAsync();
            var existing = allIndexers.FirstOrDefault(i => NormalizeIndexerUrl(i.Url) == normalized && (i.ApiKey ?? string.Empty) == (apiKey ?? string.Empty));
            return existing == null ? null : await _indexerRepository.GetByIdAsync(existing.Id);
        }

        private static Indexer CreateIndexer(ParsedProwlarrIndexerPayload parsed)
        {
            var indexer = new Indexer
            {
                Name = parsed.Name,
                Implementation = parsed.Implementation,
                Url = parsed.Url,
                ApiKey = string.IsNullOrEmpty(parsed.ApiKey) ? null : parsed.ApiKey,
                Categories = parsed.Categories ?? string.Empty,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsEnabled = true,
                Tags = string.Empty,
                AdditionalSettings = string.Empty
            };

            indexer.Type = ResolveIndexerType(indexer.Implementation);
            return indexer;
        }

        private static void ApplyParsedValues(Indexer indexer, ParsedProwlarrIndexerPayload parsed)
        {
            if (!string.IsNullOrEmpty(parsed.Name)) indexer.Name = parsed.Name;
            if (!string.IsNullOrEmpty(parsed.Implementation))
            {
                indexer.Implementation = parsed.Implementation;
                indexer.Type = ResolveIndexerType(parsed.Implementation);
            }

            if (!string.IsNullOrEmpty(parsed.Url)) indexer.Url = parsed.Url;
            indexer.ApiKey = string.IsNullOrEmpty(parsed.ApiKey) ? null : parsed.ApiKey;
            indexer.Categories = parsed.Categories ?? indexer.Categories;
            indexer.UpdatedAt = DateTime.UtcNow;
        }

        private async Task CleanupDuplicateIndexersAsync(string normalizedUrl, string apiKey)
        {
            try
            {
                var all = await _indexerRepository.GetAllAsync();
                var duplicates = all
                    .Where(i => NormalizeIndexerUrl(i.Url) == normalizedUrl && (i.ApiKey ?? string.Empty) == apiKey)
                    .OrderBy(i => i.Id)
                    .ToList();

                if (duplicates.Count <= 1) return;

                var remove = duplicates.Skip(1).ToList();
                _logger.LogInformation("Dedupe: Removing {Count} duplicate indexer(s) for url={Url}", remove.Count, normalizedUrl);

                foreach (var duplicate in remove)
                {
                    await _indexerRepository.DeleteAsync(duplicate.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to cleanup duplicate indexers for {Url}", normalizedUrl);
            }
        }

        private static string NormalizeIndexerUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath.TrimEnd('/');
                if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(0, path.Length - 4);
                }

                var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
                return $"{uri.Scheme}://{uri.Host}{port}{path}".TrimEnd('/');
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return url.TrimEnd('/');
            }
        }

        private static string ResolveIndexerType(string? implementation)
        {
            var implLower = (implementation ?? string.Empty).ToLowerInvariant();
            return implLower.Contains("newznab") ? "Usenet" : (implLower.Contains("torznab") ? "Torrent" : "Custom");
        }
    }

    public sealed record ProwlarrIndexerUpsertResult(Indexer Indexer, bool Created, bool StillExists);

    public sealed record ProwlarrIndexerBulkImportResult(int Created, int Skipped, List<Indexer> CreatedIndexers);
}
