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

namespace Listenarr.Api.Features.Search
{
    internal static class SearchMamOptionsReader
    {
        public static MyAnonamouseOptions FromQuery(IQueryCollection query)
        {
            var mamOptions = new MyAnonamouseOptions();
            if (query.TryGetValue("mamFilter", out var queryMamFilter) && Enum.TryParse<MamTorrentFilter>(queryMamFilter.ToString() ?? string.Empty, true, out var mamFilter))
                mamOptions.Filter = mamFilter;
            if (query.TryGetValue("mamSearchInDescription", out var queryMamSearchInDescription) && bool.TryParse(queryMamSearchInDescription, out var sd))
                mamOptions.SearchInDescription = sd;
            if (query.TryGetValue("mamSearchInSeries", out var queryMamSearchInSeries) && bool.TryParse(queryMamSearchInSeries, out var ss))
                mamOptions.SearchInSeries = ss;
            if (query.TryGetValue("mamSearchInFilenames", out var queryMamSearchInFilenames) && bool.TryParse(queryMamSearchInFilenames, out var sf))
                mamOptions.SearchInFilenames = sf;
            if (query.TryGetValue("mamLanguage", out var queryMamLanguage))
                mamOptions.SearchLanguage = queryMamLanguage.ToString();
            if (query.TryGetValue("mamFreeleechWedge", out var queryMamFreeleechWedge) && Enum.TryParse<MamFreeleechWedge>(queryMamFreeleechWedge.ToString() ?? string.Empty, true, out var mw))
                mamOptions.FreeleechWedge = mw;

            return mamOptions;
        }

        public static SearchRequest? FromBoundParameters(
            string? mamFilter,
            bool? mamSearchInDescription,
            bool? mamSearchInSeries,
            bool? mamSearchInFilenames,
            string? mamLanguage,
            string? mamFreeleechWedge,
            bool? mamEnrichResults,
            int? mamEnrichTopResults)
        {
            if (mamFilter == null &&
                !mamSearchInDescription.HasValue &&
                !mamSearchInSeries.HasValue &&
                !mamSearchInFilenames.HasValue &&
                mamLanguage == null &&
                mamFreeleechWedge == null &&
                !mamEnrichResults.HasValue &&
                !mamEnrichTopResults.HasValue)
            {
                return null;
            }

            var request = new SearchRequest
            {
                MyAnonamouse = new MyAnonamouseOptions()
            };

            if (mamSearchInDescription.HasValue) request.MyAnonamouse.SearchInDescription = mamSearchInDescription.Value;
            if (mamSearchInSeries.HasValue) request.MyAnonamouse.SearchInSeries = mamSearchInSeries.Value;
            if (mamSearchInFilenames.HasValue) request.MyAnonamouse.SearchInFilenames = mamSearchInFilenames.Value;
            if (!string.IsNullOrWhiteSpace(mamLanguage)) request.MyAnonamouse.SearchLanguage = mamLanguage;

            if (!string.IsNullOrWhiteSpace(mamFilter) && Enum.TryParse<MamTorrentFilter>(mamFilter, true, out var mf))
                request.MyAnonamouse.Filter = mf;

            if (!string.IsNullOrWhiteSpace(mamFreeleechWedge) && Enum.TryParse<MamFreeleechWedge>(mamFreeleechWedge, true, out var fw))
                request.MyAnonamouse.FreeleechWedge = fw;
            if (mamEnrichResults.HasValue) request.MyAnonamouse.EnrichResults = mamEnrichResults.Value;
            if (mamEnrichTopResults.HasValue) request.MyAnonamouse.EnrichTopResults = mamEnrichTopResults.Value;

            return request;
        }
    }
}
