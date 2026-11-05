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
 */
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Metadata
{
    /// <summary>
    /// Audiobook editions from catalogues beyond Audible's current search. Results use the same shape as
    /// Audible search results so Library Import can offer and import them as matches.
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/editions")]
    [Tags("Metadata")]
    public class EditionSearchController : ControllerBase
    {
        private readonly IAudiosiloMetaService _audiosilo;

        public EditionSearchController(IAudiosiloMetaService audiosilo)
        {
            _audiosilo = audiosilo;
        }

        /// <summary>
        /// Recordings of a title (optionally narrowed by author), each with narrators, runtime and an ASIN
        /// - preferring the ASIN of the requested region when the recording has one there.
        /// </summary>
        [HttpGet("search")]
        public async Task<IActionResult> Search(
            [FromQuery] string? title,
            [FromQuery] string? author,
            [FromQuery] string? region,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return BadRequest(new { message = "title is required" });
            }

            // The service never throws for lookup failures; it returns no editions instead.
            var editions = await _audiosilo.SearchEditionsAsync(title, author, cancellationToken);
            var preferredRegion = string.IsNullOrWhiteSpace(region) ? "us" : region.Trim().ToLowerInvariant();
            return Ok(editions
                .Where(edition => edition.Asins.Count > 0)
                .Select(edition => ToSearchResult(edition, preferredRegion))
                .ToList());
        }

        internal static EditionSearchResult ToSearchResult(AudiobookEditionCandidate edition, string preferredRegion)
        {
            var asin = edition.Asins.FirstOrDefault(a => a.Region == preferredRegion) ?? edition.Asins[0];
            return new EditionSearchResult
            {
                Source = edition.Source,
                SourceLink = $"https://meta.audiosilo.app/works/{Uri.EscapeDataString(edition.WorkId)}",
                Asin = asin.Asin,
                Region = asin.Region,
                Title = edition.Title,
                Authors = edition.Authors.Select(name => new EditionPerson(name)).ToList(),
                Narrators = edition.Narrators.Select(name => new EditionPerson(name)).ToList(),
                Narrator = edition.Narrators.Count > 0 ? string.Join(", ", edition.Narrators) : null,
                LengthMinutes = edition.RuntimeMinutes,
                Publisher = edition.Publisher,
                ReleaseDate = edition.ReleaseDate,
                ImageUrl = edition.CoverUrl,
                Language = edition.Language,
                Series = string.IsNullOrWhiteSpace(edition.Series)
                    ? new List<EditionSeries>()
                    : new List<EditionSeries> { new(edition.Series!, edition.SeriesPosition) },
                Abridged = edition.Abridged
            };
        }
    }
}
