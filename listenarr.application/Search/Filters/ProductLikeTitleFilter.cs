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

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Filters out results that look like physical products or have seller-like authors.
/// </summary>
public class ProductLikeTitleFilter : ISearchResultFilter
{
    public string FilterReason => "product_like_filtered";

    public bool ShouldFilter(SearchResult result)
    {
        // If this result was enriched by a metadata source (Amazon/Audible/Audible/Audnexus/OpenLibrary),
        // prefer the enriched metadata and do not treat it as a product-like false positive.
        if (result.IsEnriched && !string.IsNullOrWhiteSpace(result.MetadataSource))
        {
            return false;
        }

        return SearchValidation.IsProductLikeTitle(result.Title) || SearchValidation.IsSellerArtist(result.Artist);
    }
}
