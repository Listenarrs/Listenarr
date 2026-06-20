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

namespace Listenarr.Application.Search.Metadata;

public class MetadataSourceCatalog
{
    private readonly IApiConfigurationRepository _apiConfigRepository;
    private readonly ILogger<MetadataSourceCatalog> _logger;

    public MetadataSourceCatalog(
        IApiConfigurationRepository apiConfigRepository,
        ILogger<MetadataSourceCatalog> logger)
    {
        _apiConfigRepository = apiConfigRepository;
        _logger = logger;
    }

    public async Task<List<ApiConfiguration>> GetEnabledMetadataSourcesAsync()
    {
        try
        {
            _logger.LogDebug("Querying database for enabled metadata sources...");

            var allConfigs = await _apiConfigRepository.GetAllAsync();
            var metadataSources = allConfigs
                .Where(api => api.IsEnabled && api.Type == "metadata")
                .OrderBy(api => api.Priority)
                .ToList();

            if (metadataSources.Count > 0)
            {
                _logger.LogInformation(
                    "Retrieved {Count} enabled metadata sources: {Sources}",
                    metadataSources.Count,
                    string.Join(", ", metadataSources.Select(s => $"{s.Name} (Priority: {s.Priority}, BaseUrl: {s.BaseUrl})")));
            }
            else
            {
                _logger.LogWarning("No enabled metadata sources found in database");
            }

            return metadataSources;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid operation error retrieving enabled metadata sources");
            return new List<ApiConfiguration>();
        }
    }
}
