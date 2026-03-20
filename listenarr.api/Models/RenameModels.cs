using System;
using System.Collections.Generic;

namespace Listenarr.Api.Models
{
    // ── Request DTOs ──

    /// <summary>
    /// Request body for previewing or executing a bulk rename operation.
    /// </summary>
    public class BulkRenameRequest
    {
        public int[] AudiobookIds { get; set; } = Array.Empty<int>();
    }

    /// <summary>
    /// A single confirmed rename operation to execute for one audiobook.
    /// </summary>
    public class RenameOperation
    {
        public int AudiobookId { get; set; }

        /// <summary>New folder path for the audiobook (null = no folder change).</summary>
        public string? NewFolderPath { get; set; }

        /// <summary>Individual file renames to apply within the audiobook.</summary>
        public List<FileRenameOperation> FileRenames { get; set; } = new();
    }

    public class FileRenameOperation
    {
        public int FileId { get; set; }
        public string CurrentPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request body for executing confirmed rename operations.
    /// </summary>
    public class ExecuteRenameRequest
    {
        public List<RenameOperation> Operations { get; set; } = new();
    }

    // ── Response DTOs ──

    /// <summary>
    /// Preview of all rename changes for a single audiobook.
    /// </summary>
    public class RenamePreview
    {
        public int AudiobookId { get; set; }
        public string? AudiobookTitle { get; set; }
        public string? CurrentFolderPath { get; set; }
        public string? NewFolderPath { get; set; }
        public bool FolderChanged { get; set; }
        public List<FileRenamePreview> FileRenames { get; set; } = new();
        public bool HasChanges { get; set; }
    }

    /// <summary>
    /// Preview of a rename for a single file within an audiobook.
    /// </summary>
    public class FileRenamePreview
    {
        public int FileId { get; set; }
        public string? CurrentPath { get; set; }
        public string? NewPath { get; set; }
        public string? CurrentFilename { get; set; }
        public string? NewFilename { get; set; }
        public bool Changed { get; set; }
    }

    /// <summary>
    /// Result of executing a rename for a single audiobook.
    /// </summary>
    public class RenameResult
    {
        public int AudiobookId { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public List<FileRenameResultItem> RenamedFiles { get; set; } = new();
    }

    public class FileRenameResultItem
    {
        public int FileId { get; set; }
        public string? PreviousPath { get; set; }
        public string? NewPath { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
