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

public partial class LibraryController
{
    public class ScanRequest
    {
        public string? Path { get; set; }
    }

    public class BulkDeleteRequest
    {
        public List<int> Ids { get; set; } = [];
    }

    public enum BulkPathChangeMode
    {
        None,
        MetadataOnly,
        Physical
    }

    public class BulkPathChangeRequest
    {
        public BulkPathChangeMode Mode { get; set; }
        public string? DestinationRootOrPath { get; set; }
        public bool DeleteEmptySource { get; set; } = true;
    }

    public class BulkUpdateRequest
    {
        public List<int> Ids { get; set; } = [];
        public Dictionary<string, object> Updates { get; set; } = [];
        public BulkPathChangeRequest? PathChange { get; set; }
    }

    public class AddToLibraryRequest
    {
        public AudibleBookMetadata Metadata { get; set; } = new();
        public bool Monitored { get; set; } = true;
        public int? QualityProfileId { get; set; }
        public bool AutoSearch { get; set; }
        public string? DestinationPath { get; set; }
        public SearchResult? SearchResult { get; set; }
    }

    public class PreviewPathRequest
    {
        public AudibleBookMetadata Metadata { get; set; } = new();
        public string? DestinationRoot { get; set; }
    }

    public class MoveRequest
    {
        public string? DestinationPath { get; set; }
        /// <summary>
        /// Optional optimistic-concurrency value containing the source path observed by the caller.
        /// It must match the audiobook's current BasePath and is never used as a filesystem source override.
        /// </summary>
        public string? SourcePath { get; set; }
        public bool? MoveFiles { get; set; }
        public bool? DeleteEmptySource { get; set; }
    }
}
