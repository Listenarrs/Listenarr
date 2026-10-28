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

using Microsoft.AspNetCore.Mvc;
using System.Runtime.InteropServices;

namespace Listenarr.Api.Features.SystemDiagnostics;

[ApiController]
[Route("api/v{version:apiVersion}/filesystem")]
[Tags("File System")]
public class FileSystemController : ControllerBase
{
    private readonly ILogger<FileSystemController> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IMoveSourceCleanupPolicyResolver? _sourceCleanupPolicyResolver;
    private readonly IFileSystemVolumeResolver _volumeResolver;

    public FileSystemController(
        ILogger<FileSystemController> logger,
        IFileSystem fileSystem,
        IFileSystemVolumeResolver volumeResolver,
        IMoveSourceCleanupPolicyResolver? sourceCleanupPolicyResolver = null)
    {
        _logger = logger;
        _fileSystem = fileSystem;
        _sourceCleanupPolicyResolver = sourceCleanupPolicyResolver;
        _volumeResolver = volumeResolver
            ?? throw new ArgumentNullException(nameof(volumeResolver));
    }

    /// <summary>
    /// Browse the server file system. Returns directories and files for a given path, or root drives if no path is provided.
    /// </summary>
    /// <param name="path">Directory path to browse. Leave empty to list root drives/directories.</param>
    /// <returns>The current path, parent path, and a list of child items.</returns>
    [HttpGet("browse")]
    public ActionResult<FileSystemBrowseResponse> BrowseDirectory([FromQuery] string? path)
    {
        try
        {
            // If no path provided, return root drives/directories
            if (string.IsNullOrWhiteSpace(path))
            {
                return GetRootDirectories();
            }

            // Validate and normalize the path
            var normalizedPath = Path.GetFullPath(path);

            if (!_fileSystem.DirectoryExists(normalizedPath))
            {
                return NotFound(new { error = "Directory not found" });
            }

            var directories = new List<FileSystemItem>();
            var parent = _fileSystem.GetParentDirectory(normalizedPath);

            try
            {
                foreach (var entry in _fileSystem.EnumerateEntries(normalizedPath))
                {
                    if (entry.IsHidden || entry.IsSystem)
                    {
                        continue;
                    }

                    directories.Add(new FileSystemItem
                    {
                        Name = entry.Name,
                        Path = entry.FullPath,
                        IsDirectory = entry.IsDirectory,
                        LastModified = entry.LastWriteTime
                    });
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied to directory: {Path}", LogRedaction.SanitizeFilePath(normalizedPath));
            }

            return new FileSystemBrowseResponse
            {
                CurrentPath = normalizedPath,
                ParentPath = parent,
                Items = directories.OrderByDescending(d => d.IsDirectory).ThenBy(d => d.Name).ToList()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error browsing directory: {Path}", LogRedaction.SanitizeFilePath(path));
            return StatusCode(500, new { error = "Error browsing directory" });
        }
    }

    /// <summary>
    /// Validate a file-system path, checking whether it exists and is writable.
    /// </summary>
    /// <param name="path">The absolute directory path to validate.</param>
    /// <returns>Validation result with existence and writability flags.</returns>
    [HttpGet("validate")]
    public ActionResult<FileSystemValidateResponse> ValidatePath([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new FileSystemValidateResponse
                {
                    IsValid = false,
                    Message = "Path cannot be empty"
                };
            }

            var normalizedPath = Path.GetFullPath(path);
            var exists = _fileSystem.DirectoryExists(normalizedPath);
            var isWritable = false;

            if (exists)
            {
                try
                {
                    // Try to create a temporary file to check write permissions
                    var testFile = Path.Join(normalizedPath, $".listenarr_test_{Guid.NewGuid()}.tmp");
                    if (!_fileSystem.TryValidateMutationTarget(testFile, [normalizedPath], out var safeTestFile, out _))
                    {
                        isWritable = false;
                    }
                    else
                    {
                        _fileSystem.WriteAllText(safeTestFile, "test");
                        _fileSystem.DeleteFile(safeTestFile);
                        isWritable = true;
                    }
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                {
                    isWritable = false;
                }
            }

            return new FileSystemValidateResponse
            {
                IsValid = exists && isWritable,
                Exists = exists,
                IsWritable = isWritable,
                Message = !exists ? "Directory does not exist" :
                         !isWritable ? "Directory is not writable" :
                         "Directory is valid"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error validating path: {Path}", LogRedaction.SanitizeFilePath(path));
            return new FileSystemValidateResponse
            {
                IsValid = false,
                Message = $"Error validating path: {ex.Message}"
            };
        }
    }

    private FileSystemBrowseResponse GetRootDirectories()
    {
        var items = _fileSystem
            .EnumerateRoots()
            .Select(root => new FileSystemItem
            {
                Name = root.Name,
                Path = root.FullPath,
                IsDirectory = true,
                LastModified = root.LastWriteTime
            })
            .ToList();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var commonDirs = new HashSet<string>(["/home", "/mnt", "/media", "/opt"], StringComparer.Ordinal);
            items.AddRange(_fileSystem
                .EnumerateEntries("/")
                .Where(entry => entry.IsDirectory && commonDirs.Contains(entry.FullPath))
                .Select(entry => new FileSystemItem
                {
                    Name = entry.Name,
                    Path = entry.FullPath,
                    IsDirectory = true,
                    LastModified = entry.LastWriteTime
                }));
        }

        return new FileSystemBrowseResponse
        {
            CurrentPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Computer" : "/",
            ParentPath = null,
            Items = items
        };
    }

    /// <summary>
    /// Check whether two paths reside on the same volume. Moving files across volumes will break hardlinks.
    /// </summary>
    /// <param name="sourcePath">Source directory path.</param>
    /// <param name="destPath">Destination directory path.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Volume comparison result including a warning if hardlinks will be broken.</returns>
    [HttpGet("check-volume")]
    public async Task<ActionResult<VolumeCheckResponse>> CheckVolume(
        [FromQuery] string? sourcePath,
        [FromQuery] string? destPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
            {
                return Ok(new VolumeCheckResponse
                {
                    SameVolume = false,
                    WillBreakHardlinks = true,
                    Message = "Source or destination path not provided"
                });
            }

            var comparison = _volumeResolver.Compare(sourcePath, destPath);
            var sourceRoot = comparison.SourceBoundary
                ?? Path.GetPathRoot(Path.GetFullPath(sourcePath));
            var destRoot = comparison.DestinationBoundary
                ?? Path.GetPathRoot(Path.GetFullPath(destPath));
            var sameVolume = comparison.IsAvailable && comparison.SameVolume;

            var cleanup = _sourceCleanupPolicyResolver == null
                ? null
                : await _sourceCleanupPolicyResolver.ResolveAsync(
                    sourcePath,
                    destPath,
                    cancellationToken);
            return Ok(new VolumeCheckResponse
            {
                SameVolume = sameVolume,
                WillBreakHardlinks = !sameVolume,
                SourceVolume = sourceRoot,
                DestVolume = destRoot,
                VerifiedSourceDeletionEnabled = cleanup?.DeletesSourceAfterVerifiedCopy ?? false,
                ForceCopyAndRetainSource = cleanup?.ForceCopyAndRetainSource ?? false,
                SourceIsManagedRoot = cleanup?.SourceIsManagedRoot ?? false,
                SourceCleanupMessage = cleanup?.Message,
                Message = !comparison.IsAvailable
                    ? "Unable to prove that the paths are on the same volume; copy behavior will be assumed."
                    : sameVolume
                    ? "Paths are on the same volume"
                    : "Moving across volumes will break hardlinks and create independent copies"
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error checking volume for paths: {Source} -> {Dest}", LogRedaction.SanitizeFilePath(sourcePath), LogRedaction.SanitizeFilePath(destPath));
            return Ok(new VolumeCheckResponse
            {
                SameVolume = false,
                WillBreakHardlinks = true,
                Message = "Unable to determine volume information"
            });
        }
    }
}

public class FileSystemBrowseResponse
{
    public string CurrentPath { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public List<FileSystemItem> Items { get; set; } = new();
}

public class FileSystemItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public DateTime LastModified { get; set; }
}

public class FileSystemValidateResponse
{
    public bool IsValid { get; set; }
    public bool Exists { get; set; }
    public bool IsWritable { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class VolumeCheckResponse
{
    public bool SameVolume { get; set; }
    public bool WillBreakHardlinks { get; set; }
    public string? SourceVolume { get; set; }
    public string? DestVolume { get; set; }
    public string? Message { get; set; }
    public bool VerifiedSourceDeletionEnabled { get; set; }
    public bool ForceCopyAndRetainSource { get; set; }
    public bool SourceIsManagedRoot { get; set; }
    public string? SourceCleanupMessage { get; set; }
}
