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

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.DomainRotation
{
    public class IndexerDomainRotationService(
        IApplicationSettingsRepository settingsRepo,
        IIndexerRepository indexerRepo,
        IDomainReachabilityChecker checker,
        ILogger<IndexerDomainRotationService> logger) : IIndexerDomainRotationService
    {
        public async Task<RotationResult> EnsureReachableAsync(Indexer indexer, CancellationToken ct)
        {
            var settings = await settingsRepo.GetAsync(ct);
            if (settings == null || !settings.EnableIndexerDomainRotation)
                return new RotationResult(false, null, "rotation disabled");

            if (!IndexerMirrorCatalog.IsKnown(indexer.Implementation))
                return new RotationResult(false, null, $"implementation '{indexer.Implementation}' not in catalog");

            if (!indexer.IsEnabled)
                return new RotationResult(false, null, "indexer disabled");

            if (!Uri.TryCreate(indexer.Url, UriKind.Absolute, out var currentUri))
                return new RotationResult(false, null, "invalid indexer URL");

            var scheme = currentUri.Scheme;

            if (await checker.IsReachableAsync(scheme, currentUri.Host, ct))
                return new RotationResult(false, null, "current host reachable");

            logger.LogInformation("Indexer '{Name}' (ID {Id}): {Host} unreachable, searching mirrors",
                indexer.Name, indexer.Id, currentUri.Host);

            var candidates = BuildCandidateHosts(indexer, settings, currentUri);
            foreach (var host in candidates)
            {
                if (ct.IsCancellationRequested) break;
                if (await checker.IsReachableAsync(scheme, host, ct))
                {
                    var oldHost = currentUri.Host;
                    indexer.Url = ReplaceHost(currentUri, host);
                    await indexerRepo.UpdateAsync(indexer, ct);
                    logger.LogInformation("Rotated indexer '{Name}' (ID {Id}) from {OldHost} to {NewHost}",
                        indexer.Name, indexer.Id, oldHost, host);
                    return new RotationResult(true, host, $"rotated from {oldHost} to {host}");
                }
            }

            logger.LogWarning("Indexer '{Name}' (ID {Id}): no working mirror found among {Count} candidates",
                indexer.Name, indexer.Id, candidates.Count);
            return new RotationResult(false, null, "no working mirror found");
        }

        /// <summary>
        /// Candidate list = [current host] + seeds + user overrides, deduplicated.
        /// Current host is tested first by the caller before this list is used.
        /// </summary>
        private List<string> BuildCandidateHosts(Indexer indexer, ApplicationSettings settings, Uri currentUri)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            void Add(string host)
            {
                if (seen.Add(host)) result.Add(host);
            }

            // seeds (current host already tested, but including it keeps the list consistent)
            Add(currentUri.Host);

            if (IndexerMirrorCatalog.Seeds.TryGetValue(indexer.Implementation, out var seeds))
                foreach (var h in seeds) Add(h);

            if (!string.IsNullOrWhiteSpace(settings.IndexerMirrorOverridesJson))
            {
                try
                {
                    var overrides = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                        settings.IndexerMirrorOverridesJson);
                    if (overrides != null && overrides.TryGetValue(indexer.Implementation, out var extras))
                        foreach (var h in extras) Add(h);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Failed to parse IndexerMirrorOverridesJson; skipping user overrides");
                }
            }

            // skip current host in the iteration — it was already tested above
            result.Remove(currentUri.Host);
            return result;
        }

        private static string ReplaceHost(Uri original, string newHost)
        {
            var builder = new UriBuilder(original) { Host = newHost };
            if (original.IsDefaultPort) builder.Port = -1;
            return builder.Uri.ToString();
        }
    }
}
