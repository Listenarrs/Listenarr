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
namespace Listenarr.Api.Features.Metadata
{
    public sealed class EditionSearchResult
    {
        public string Source { get; init; } = string.Empty;
        public string? SourceLink { get; init; }
        public string Asin { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public List<EditionPerson> Authors { get; init; } = new();
        public List<EditionPerson> Narrators { get; init; } = new();
        public string? Narrator { get; init; }
        public int? LengthMinutes { get; init; }
        public string? Publisher { get; init; }
        public string? ReleaseDate { get; init; }
        public string? ImageUrl { get; init; }
        public string? Language { get; init; }
        public List<EditionSeries> Series { get; init; } = new();
        public bool? Abridged { get; init; }
    }

    public sealed record EditionPerson(string Name);

    public sealed record EditionSeries(string Name, string? Position);
}
