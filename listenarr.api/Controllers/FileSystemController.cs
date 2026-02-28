/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

namespace Listenarr.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FileSystemController : ControllerBase
{
    private readonly ILogger<FileSystemController> _logger;

    public FileSystemController(ILogger<FileSystemController> logger)
    {
        _logger = logger;
    }

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

            if (!Directory.Exists(normalizedPath))
            {
                return NotFound(new { error = "Directory not found" });
            }

            var directories = new List<FileSystemItem>();
            var parent = Directory.GetParent(normalizedPath);

            try
            {
                // Get directories and files in the current path
                var dirInfo = new DirectoryInfo(normalizedPath);
                
                // Add directories
                foreach (var dir in dirInfo.GetDirectories())
                {
                    // Skip hidden and system directories
                    if ((dir.Attributes & FileAttributes.Hidden) != 0 ||
                        (dir.Attributes & FileAttributes.System) != 0)
                    {
                        continue;
                    }

                    directories.Add(new FileSystemItem
                    {
                        Name = dir.Name,
                        Path = dir.FullName,
                        IsDirectory = true,
                        LastModified = dir.LastWriteTime
                    });
                }

                // Add files
                foreach (var file in dirInfo.GetFiles())
                {
                    // Skip hidden and system files
                    if ((file.Attributes & FileAttributes.Hidden) != 0 ||
                        (file.Attributes & FileAttributes.System) != 0)
                    {
                        continue;
                    }

                    directories.Add(new FileSystemItem
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        IsDirectory = false,
                        LastModified = file.LastWriteTime
                    });
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied to directory: {Path}", normalizedPath);
            }

            return new FileSystemBrowseResponse
            {
                CurrentPath = normalizedPath,
                ParentPath = parent?.FullName,
                Items = directories.OrderByDescending(d => d.IsDirectory).ThenBy(d => d.Name).ToList()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error browsing directory: {Path}", path);
            return StatusCode(500, new { error = "Error browsing directory" });
        }
    }

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
            var exists = Directory.Exists(normalizedPath);
            var isWritable = false;

            if (exists)
            {
                try
                {
                    // Try to create a temporary file to check write permissions
                    var testFile = Path.Combine(normalizedPath, $".listenarr_test_{Guid.NewGuid()}.tmp");
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    isWritable = true;
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
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
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error validating path: {Path}", path);
            return new FileSystemValidateResponse
            {
                IsValid = false,
                Message = $"Error validating path: {ex.Message}"
            };
        }
    }

    private FileSystemBrowseResponse GetRootDirectories()
    {
        var items = new List<FileSystemItem>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Get all drives on Windows
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                {
                    items.Add(new FileSystemItem
                    {
                        Name = $"{drive.Name} ({drive.VolumeLabel})",
                        Path = drive.Name,
                        IsDirectory = true,
                        LastModified = DateTime.Now
                    });
                }
            }
        }
        else
        {
            // Unix-like systems start at root
            items.Add(new FileSystemItem
            {
                Name = "/",
                Path = "/",
                IsDirectory = true,
                LastModified = DateTime.Now
            });

            // Add common directories
            var commonDirs = new[] { "/home", "/mnt", "/media", "/opt" };
            foreach (var dir in commonDirs)
            {
                if (Directory.Exists(dir))
                {
                    var dirInfo = new DirectoryInfo(dir);
                    items.Add(new FileSystemItem
                    {
                        Name = dirInfo.Name,
                        Path = dirInfo.FullName,
                        IsDirectory = true,
                        LastModified = dirInfo.LastWriteTime
                    });
                }
            }
        }

        return new FileSystemBrowseResponse
        {
            CurrentPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Computer" : "/",
            ParentPath = null,
            Items = items
        };
    }

    [HttpGet("check-volume")]
    public ActionResult<VolumeCheckResponse> CheckVolume([FromQuery] string? sourcePath, [FromQuery] string? destPath)
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

            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
            var destRoot = Path.GetPathRoot(Path.GetFullPath(destPath));

            var sameVolume = string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase);

            return Ok(new VolumeCheckResponse
            {
                SameVolume = sameVolume,
                WillBreakHardlinks = !sameVolume,
                SourceVolume = sourceRoot,
                DestVolume = destRoot,
                Message = sameVolume 
                    ? "Paths are on the same volume" 
                    : "⚠️ Moving across volumes will break hardlinks and create independent copies"
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error checking volume for paths: {Source} -> {Dest}", sourcePath, destPath);
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
}

