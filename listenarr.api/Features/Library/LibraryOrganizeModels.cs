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

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Bucket assigned to each audiobook in the organize-library preview.
    /// </summary>
    public static class OrganizePreviewStatus
    {
        public const string AlreadyCanonical = "already_canonical";
        public const string WillMove = "will_move";
        public const string Collision = "collision";
        public const string InvalidTarget = "invalid_target";
    }

    /// <summary>
    /// Machine-readable code accompanying an <c>invalid_target</c> row's
    /// human-readable <see cref="OrganizePreviewRowDto.Reason"/>. Lets the UI
    /// group rows by failure kind and pick the right "how to resolve" copy /
    /// action without parsing the prose reason string (the prose may be
    /// reworded freely without breaking the frontend or tests).
    /// </summary>
    public static class OrganizeInvalidReasonCode
    {
        /// <summary>Title metadata is blank — no canonical path can be built.</summary>
        public const string MissingTitle = "missing_title";
        /// <summary>Author metadata is blank — no canonical path can be built.</summary>
        public const string MissingAuthor = "missing_author";
        /// <summary>No <c>FolderNamingPattern</c> is configured.</summary>
        public const string PatternNotConfigured = "pattern_not_configured";
        /// <summary>Current path lives outside every configured library root.</summary>
        public const string OutsideRoot = "outside_root";
        /// <summary>The naming pattern rendered to an empty path for this row.</summary>
        public const string EmptyPattern = "empty_pattern";
        /// <summary>BasePath equals a library root folder — re-scan needed.</summary>
        public const string SourceAtRoot = "source_at_root";
        /// <summary>Target is an ancestor of source (move would flatten/nest-collapse).</summary>
        public const string TargetAncestor = "target_ancestor";
        /// <summary>Target directory already exists on disk and contains files.</summary>
        public const string TargetExists = "target_exists";
        /// <summary>
        /// An ancestor/nested row whose source folder no longer exists on disk —
        /// the record's files are gone. Re-scan the library or remove the record.
        /// </summary>
        public const string SourceMissing = "source_missing";
    }

    /// <summary>
    /// One row in the organize-library preview. Computed by applying the
    /// configured <c>FolderNamingPattern</c> to the audiobook's metadata and
    /// comparing the resulting path against the current <c>BasePath</c>.
    /// </summary>
    public class OrganizePreviewRowDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? CurrentPath { get; set; }
        public string? TargetPath { get; set; }
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        /// <summary>One of <see cref="OrganizePreviewStatus"/>.</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>
        /// Set when <see cref="Status"/> is <c>collision</c> — the normalized
        /// target path shared by multiple audiobooks. Lets the UI group
        /// collision rows together.
        /// </summary>
        public string? CollisionKey { get; set; }
        /// <summary>
        /// Set when <see cref="Status"/> is <c>invalid_target</c> — the
        /// reason the target couldn't be computed (e.g. "Missing author").
        /// </summary>
        public string? Reason { get; set; }
        /// <summary>
        /// Set when <see cref="Status"/> is <c>invalid_target</c> — the
        /// machine-readable companion to <see cref="Reason"/>. One of
        /// <see cref="OrganizeInvalidReasonCode"/>. The UI groups invalid rows
        /// by this code and renders per-kind resolution guidance.
        /// </summary>
        public string? ReasonCode { get; set; }
        /// <summary>
        /// True only for <c>invalid_target</c> rows whose <see cref="ReasonCode"/>
        /// is <c>target_ancestor</c> and that pass the read-only flatten
        /// feasibility check (source exists on disk, target holds no foreign
        /// files). The UI shows the one-click "Flatten" action only when this is
        /// true, so it never offers a flatten the executor would refuse.
        /// </summary>
        public bool CanFlatten { get; set; }
        /// <summary>
        /// True for <c>will_move</c> rows whose target directory exists on disk
        /// but holds only leftover metadata (no audio files, nothing referenced
        /// by the DB). The move will delete and replace the stub instead of
        /// refusing; the UI labels these so the operator knows the target isn't
        /// pristine.
        /// </summary>
        public bool ReplacesStubTarget { get; set; }
    }

    public class OrganizeLibraryPreviewDto
    {
        public List<OrganizePreviewRowDto> Rows { get; set; } = new();
        public int AlreadyCanonicalCount { get; set; }
        public int WillMoveCount { get; set; }
        public int CollisionCount { get; set; }
        public int InvalidTargetCount { get; set; }
    }

    /// <summary>
    /// Apply-endpoint body. Caller passes the explicit ids confirmed in the
    /// preview UI; the server re-validates each one before queuing.
    /// </summary>
    public class OrganizeLibraryApplyRequest
    {
        public List<int> AudiobookIds { get; set; } = new();
    }

    public class OrganizeApplySkippedDto
    {
        public int AudiobookId { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// One queued background move surfaced in the apply response. The
    /// frontend uses <see cref="JobId"/> to correlate SignalR
    /// <c>MoveJobUpdate</c> events back to the specific audiobook so a
    /// failure's <c>error</c> string lands next to the right book in the
    /// results UI.
    /// </summary>
    public class OrganizeQueuedJobDto
    {
        public string JobId { get; set; } = string.Empty;
        public int AudiobookId { get; set; }
        public string? AudiobookTitle { get; set; }
        public string? TargetPath { get; set; }
    }

    /// <summary>
    /// Body for the organize "flatten" action — collapse a single
    /// nested-one-level-too-deep row into its canonical parent folder.
    /// </summary>
    public class OrganizeFlattenRequest
    {
        public int AudiobookId { get; set; }
    }

    public class OrganizeFlattenResultDto
    {
        public bool Success { get; set; }
        public int FilesMoved { get; set; }
        public string? NewPath { get; set; }
        public string? Error { get; set; }
    }

    public class OrganizeLibraryApplyResultDto
    {
        public int Queued { get; set; }
        public int Skipped { get; set; }
        public int FailedToQueue { get; set; }
        public List<OrganizeQueuedJobDto> QueuedJobs { get; set; } = new();
        public List<OrganizeApplySkippedDto> SkippedDetails { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
