/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Api.Features.Library;

/// <summary>
/// Partial audiobook metadata update contract. Nullable value types distinguish
/// an omitted field from an explicit false, zero, or null-like update sentinel.
/// </summary>
public sealed record AudiobookUpdateRequest
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public List<string>? Authors { get; init; }
    public string? ImageUrl { get; init; }
    public string? PublishYear { get; init; }
    public string? PublishedDate { get; init; }
    public string? Series { get; init; }
    public string? SeriesNumber { get; init; }
    public List<AudiobookSeriesMembership>? SeriesMemberships { get; init; }
    public string? Description { get; init; }
    public List<string>? Genres { get; init; }
    public List<string>? Tags { get; init; }
    public List<string>? Narrators { get; init; }
    public List<string>? Isbn { get; init; }
    public string? Asin { get; init; }
    public string? OpenLibraryId { get; init; }
    public string? Publisher { get; init; }
    public string? Language { get; init; }
    public int? Runtime { get; init; }
    public string? Edition { get; init; }
    public string? Version { get; init; }
    public bool? Explicit { get; init; }
    public bool? Abridged { get; init; }
    public bool? Monitored { get; init; }
    public string? FilePath { get; init; }
    public long? FileSize { get; init; }
    public string? BasePath { get; init; }
    public string? Quality { get; init; }
    public int? QualityProfileId { get; init; }
}
