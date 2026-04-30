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
namespace Listenarr.Api.Models
{
    public class BulkRenameRequest
    {
        public int[] AudiobookIds { get; set; } = Array.Empty<int>();
    }

    public class RenameOperation
    {
        public int AudiobookId { get; set; }
        public string? NewFolderPath { get; set; }
        public List<FileRenameOperation> FileRenames { get; set; } = new();
    }

    public class FileRenameOperation
    {
        public int FileId { get; set; }
        public string CurrentPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
    }

    public class ExecuteRenameRequest
    {
        public List<RenameOperation> Operations { get; set; } = new();
    }

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

    public class FileRenamePreview
    {
        public int FileId { get; set; }
        public string? CurrentPath { get; set; }
        public string? NewPath { get; set; }
        public string? CurrentFilename { get; set; }
        public string? NewFilename { get; set; }
        public bool Changed { get; set; }
    }

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
