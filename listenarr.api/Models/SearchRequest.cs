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
namespace Listenarr.Api.Models
{
    public enum SearchMode
    {
        Simple,
        Advanced
    }

    public class Pagination
    {
        public int Page { get; set; } = 1;
        public int Limit { get; set; } = 50;
    }

    public class SearchRequest
    {
        public SearchMode Mode { get; set; } = SearchMode.Simple;

        // Simple free-text query (used by simple mode)
        public string? Query { get; set; }

        // Advanced fields
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Asin { get; set; }
        public string? Isbn { get; set; }
        public string? Series { get; set; }

        public Pagination? Pagination { get; set; } = new Pagination();

        public string Region { get; set; } = "us";
        public string? Language { get; set; }

        // MyAnonamouse-specific search options (optional)
        public MyAnonamouseOptions? MyAnonamouse { get; set; }

        // If true, controller will enrich results with metadata (may incur additional requests)
        public bool IncludeEnrichment { get; set; } = true;

        // Optional cap on number of results to return
        public int? Cap { get; set; }
    }

    // Options to control MyAnonamouse (MyAnonamouse.net) search behavior
    public class MyAnonamouseOptions
    {
        // Torrent filter selection (Prowlarr-like choices)
        public MamTorrentFilter? Filter { get; set; }

        // Search text in description (default: false in Prowlarr)
        public bool? SearchInDescription { get; set; }

        // Search text in series (default: true in Prowlarr)
        public bool? SearchInSeries { get; set; }

        // Search text in filenames (default: true in Prowlarr)
        public bool? SearchInFilenames { get; set; }

        // Numeric language id to use for tor[browse_lang][] (provider-specific)
        public string? SearchLanguage { get; set; }

        // Use freeleech wedge preference
        public MamFreeleechWedge? FreeleechWedge { get; set; }

        // Optional: perform per-result enrichment by fetching item pages for missing fields (disabled by default)
        public bool? EnrichResults { get; set; }
        // Maximum number of results to enrich for a single search when EnrichResults is true (default: 3)
        public int? EnrichTopResults { get; set; }
    }

    public enum MamTorrentFilter
    {
        SearchEverything,
        Active,
        Freeleech,
        FreeleechOrVip,
        Vip,
        NotVip
    }

    public enum MamFreeleechWedge
    {
        Never,
        Preferred,
        Required
    }
}
