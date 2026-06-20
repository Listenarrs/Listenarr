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

namespace Listenarr.Application.Search.Indexers.Common;

public class IndexerAdditionalSettingsParser
{
    private readonly ILogger<IndexerAdditionalSettingsParser> _logger;

    public IndexerAdditionalSettingsParser(ILogger<IndexerAdditionalSettingsParser> logger)
    {
        _logger = logger;
    }

    public MyAnonamouseOptions? ParseMamOptions(string? additional)
    {
        if (string.IsNullOrWhiteSpace(additional)) return null;
        try
        {
            using var doc = JsonDocument.Parse(additional);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var opts = new MyAnonamouseOptions();
            if (root.TryGetProperty("mam_options", out var mo) && mo.ValueKind == JsonValueKind.Object)
            {
                ApplyProperties(mo, opts);
                return opts;
            }

            ApplyProperties(root, opts);

            if (opts.SearchInDescription == null
                && opts.SearchInSeries == null
                && opts.SearchInFilenames == null
                && opts.SearchLanguage == null
                && opts.Filter == null
                && opts.FreeleechWedge == null
                && opts.EnrichResults == null
                && opts.EnrichTopResults == null)
            {
                return null;
            }

            return opts;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse AdditionalSettings JSON for MAM options");
            return null;
        }
    }

    private static void ApplyProperties(JsonElement root, MyAnonamouseOptions opts)
    {
        if (root.TryGetProperty("searchInDescription", out var sid) && IsBoolean(sid))
            opts.SearchInDescription = sid.GetBoolean();
        if (root.TryGetProperty("searchInSeries", out var sis) && IsBoolean(sis))
            opts.SearchInSeries = sis.GetBoolean();
        if (root.TryGetProperty("searchInFilenames", out var sif) && IsBoolean(sif))
            opts.SearchInFilenames = sif.GetBoolean();
        if (root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String)
            opts.SearchLanguage = lang.GetString();
        if (root.TryGetProperty("filter", out var filter)
            && filter.ValueKind == JsonValueKind.String
            && Enum.TryParse<MamTorrentFilter>(filter.GetString() ?? string.Empty, true, out var f))
            opts.Filter = f;
        if (root.TryGetProperty("freeleechWedge", out var wedge)
            && wedge.ValueKind == JsonValueKind.String
            && Enum.TryParse<MamFreeleechWedge>(wedge.GetString() ?? string.Empty, true, out var w))
            opts.FreeleechWedge = w;
        if (root.TryGetProperty("enrichResults", out var enrich) && IsBoolean(enrich))
            opts.EnrichResults = enrich.GetBoolean();
        if (root.TryGetProperty("enrichTopResults", out var enrichTop)
            && (enrichTop.ValueKind == JsonValueKind.Number || enrichTop.ValueKind == JsonValueKind.String))
        {
            if (enrichTop.ValueKind == JsonValueKind.Number) opts.EnrichTopResults = enrichTop.GetInt32();
            else if (int.TryParse(enrichTop.GetString(), out var parsed)) opts.EnrichTopResults = parsed;
        }
    }

    private static bool IsBoolean(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False;
    }
}
