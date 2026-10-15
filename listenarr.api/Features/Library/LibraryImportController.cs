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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    [ApiController]
    [Route("api/v{version:apiVersion}/library-import")]
    [Tags("Library")]
    public class LibraryImportController : ControllerBase
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
        private readonly StructuredSearchWorkflow _searchWorkflow;

        public LibraryImportController(StructuredSearchWorkflow searchWorkflow)
        {
            _searchWorkflow = searchWorkflow;
        }

        public sealed class MatchRequest
        {
            public string? Asin { get; set; }
            public string? Title { get; set; }
            public string? Author { get; set; }
            // An Audible region; the configured one when empty
            public string? Region { get; set; }
        }

        /// <summary>
        /// Find the Audible record of a Library Import row (Start Matching): the ASIN is looked up when
        /// given, otherwise the title and author are searched, in one region only. Returns the results and
        /// the one an automatic match takes (a result found by ASIN, or the first whose title fits the
        /// searched name, preferring the given author), or none.
        /// </summary>
        /// <param name="request">The row's query (an unmatched item's searchQuery) and the region.</param>
        [HttpPost("match")]
        public async Task<IActionResult> Match([FromBody] MatchRequest request)
        {
            var query = new BookSearchQuery
            {
                Asin = BookSearchRules.LooksLikeAsin(request.Asin) ? request.Asin!.Trim().ToUpperInvariant() : null,
                Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(),
                Author = string.IsNullOrWhiteSpace(request.Author) ? null : request.Author.Trim()
            };
            if (query.Asin == null && query.Title == null)
            {
                return BadRequest(new { message = "An ASIN or a title is required." });
            }

            var body = new Dictionary<string, object?> { ["mode"] = "Advanced" };
            if (query.Asin != null)
            {
                body["asin"] = query.Asin;
                body["cap"] = 5;
            }
            else
            {
                body["title"] = query.Title;
                if (query.Author != null) body["author"] = query.Author;
                body["cap"] = 8;
            }

            if (!string.IsNullOrWhiteSpace(request.Region)) body["region"] = request.Region.Trim();

            var result = await _searchWorkflow.ExecuteAsync(
                JsonSerializer.SerializeToElement(body, WebJson), simplified: null, HttpContext);
            if (!result.Succeeded)
            {
                return BadRequest(result.Payload);
            }

            var candidates = JsonSerializer.SerializeToElement(result.Payload, WebJson) is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray().ToList()
                : new List<JsonElement>();
            var index = BookSearchRules.PickAutoMatch(candidates.Select(ToCandidate).ToList(), query);

            return Ok(new
            {
                query,
                candidates,
                match = index >= 0 ? candidates[index] : (JsonElement?)null
            });
        }

        private static BookSearchRules.MatchCandidate ToCandidate(JsonElement result)
        {
            string? Text(JsonElement element, string name) =>
                element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var series = new List<string?>();
            if (result.TryGetProperty("series", out var seriesValue))
            {
                if (seriesValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in seriesValue.EnumerateArray())
                    {
                        series.Add(Text(entry, "name"));
                        series.Add(Text(entry, "position"));
                    }
                }
                else if (seriesValue.ValueKind == JsonValueKind.String)
                {
                    series.Add(seriesValue.GetString());
                }
            }

            series.Add(Text(result, "seriesNumber"));
            string? firstAuthor = null;
            if (result.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
            {
                var first = authors.EnumerateArray().FirstOrDefault();
                firstAuthor = first.ValueKind == JsonValueKind.String ? first.GetString() : Text(first, "name");
            }

            return new BookSearchRules.MatchCandidate(
                Text(result, "asin"),
                Text(result, "title"),
                Text(result, "subtitle"),
                series,
                firstAuthor);
        }
    }
}
