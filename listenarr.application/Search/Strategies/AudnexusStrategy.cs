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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Strategies;

/// <summary>
/// Fetches metadata from Audnexus API (audnex.us).
/// </summary>
public class AudnexusStrategy : IMetadataStrategy
{
    private readonly IAudnexusService _audnexusService;
    private readonly MetadataConverters _metadataConverters;
    private readonly ILogger<AudnexusStrategy> _logger;

    public string SourceName => "Audnexus";

    public AudnexusStrategy(
        IAudnexusService audnexusService,
        MetadataConverters metadataConverters,
        ILogger<AudnexusStrategy> logger)
    {
        _audnexusService = audnexusService;
        _metadataConverters = metadataConverters;
        _logger = logger;
    }

    public bool CanHandle(ApiConfiguration source)
    {
        return source.BaseUrl.Contains("audnex.us", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AudibleBookMetadata?> FetchMetadataAsync(string asin, ApiConfiguration source, string? originalSource)
    {
        _logger.LogDebug("Calling Audnexus service for ASIN {Asin}", asin);
        var audnexusData = await _audnexusService.GetBookMetadataAsync(asin, "us", true, false);

        if (audnexusData != null)
        {
            _logger.LogInformation("✓ Audnexus returned data for ASIN {Asin}. Title: {Title}", asin, audnexusData.Title ?? "null");
            var metadata = _metadataConverters.ConvertAudnexusToMetadata(audnexusData, asin, originalSource ?? "Audible");
            _logger.LogInformation("Successfully enriched ASIN {Asin} with metadata from {SourceName}", asin, source.Name);
            return metadata;
        }
        else
        {
            _logger.LogWarning("✗ Audnexus returned null for ASIN {Asin}", asin);
        }

        return null;
    }
}
