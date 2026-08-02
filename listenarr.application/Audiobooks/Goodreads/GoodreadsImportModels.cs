/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Audiobooks.Goodreads;

public sealed class GoodreadsImportRequest
{
    public string? Url { get; set; }
    public string? CsvContent { get; set; }
    public bool Monitored { get; set; } = true;
    public int? QualityProfileId { get; set; }
    public bool AutoSearch { get; set; }
    public string? DestinationPath { get; set; }
    public int? Limit { get; set; }
}

public sealed class GoodreadsImportBook
{
    public int SourceIndex { get; set; }
    public string? GoodreadsId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public List<string> Isbn { get; set; } = [];
    public string? PublishYear { get; set; }
    public string? PublishedDate { get; set; }
    public string? Bookshelf { get; set; }
    public string? SourceUrl { get; set; }
}

public sealed class GoodreadsImportResult
{
    public int Total { get; set; }
    public int AddedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public List<GoodreadsImportRowResult> Items { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class GoodreadsImportRowResult
{
    public int SourceIndex { get; set; }
    public string? GoodreadsId { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? AudiobookId { get; set; }
}
