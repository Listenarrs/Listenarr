/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Runtime.InteropServices;
using System.Diagnostics;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem
{
    public partial class FileMover : IFileMover
    {
        public Task<bool> CopyDirectoryAsync(string sourceDir, string destDir)
        {
            try
            {
                CopyDirRecursive(sourceDir, destDir);
                LogMutation(FileMutationOutcome.Success, FileAction.Copy, sourceDir, destDir);
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Copy directory failed: {Source} -> {Dest}", sourceDir, destDir);
                return Task.FromResult(false);
            }
        }

        public async Task<bool> CopyFileAsync(string sourceFile, string destFile)
        {
            try
            {
                if (await TrySkipSameContentAsync(FileAction.Copy, sourceFile, destFile))
                {
                    return true;
                }

                File.Copy(sourceFile, destFile, true);
                LogMutation(FileMutationOutcome.Success, FileAction.Copy, sourceFile, destFile);
                return true;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogError(exception, $"Copy file failed: {sourceFile} -> {destFile}");
                return false;
            }
        }

        public async Task<bool> HardlinkFileAsync(string sourceFile, string destFile)
        {
            try
            {
                // Ensure destination directory exists
                var destDir = Path.GetDirectoryName(destFile) ?? string.Empty;
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                if (await TrySkipSameContentAsync(FileAction.HardlinkCopy, sourceFile, destFile))
                {
                    return true;
                }

                // Safe ordering: hardlink/copy to a temp path first, then atomically rename
                // onto the destination. This ensures the original destFile is never deleted
                // until we have a confirmed replacement ready.
                // Use Path.GetFileName to ensure the random name has no separators (satisfies static analysis).
                // Use Path.Join (not Path.Combine) to prevent a rooted second arg from silently discarding destDir.
                var tempDestName = Path.GetFileName(Path.GetRandomFileName()) + ".tmp";
                var tempDest = Path.Join(destDir, tempDestName);
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (!CreateHardLinkNative(tempDest, sourceFile, IntPtr.Zero))
                        {
                            var error = Marshal.GetLastWin32Error();
                            throw new IOException($"CreateHardLink failed with error code {error}");
                        }
                    }
                    else
                    {
                        // Unix/Linux/macOS
                        var result = LinkNative(sourceFile, tempDest);
                        if (result != 0)
                        {
                            var error = Marshal.GetLastWin32Error();
                            throw new IOException($"link() failed with error code {error}");
                        }
                    }

                    // Hardlink succeeded — atomically replace destination
                    File.Move(tempDest, destFile, overwrite: true);
                    LogMutation(FileMutationOutcome.Success, FileAction.HardlinkCopy, sourceFile, destFile);
                    return true;
                }
                catch (Exception linkEx) when (linkEx is not OperationCanceledException && linkEx is not OutOfMemoryException && linkEx is not StackOverflowException)
                {
                    // Clean up temp file if hardlink left one behind
                    try { if (File.Exists(tempDest)) File.Delete(tempDest); }
                    catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException
                                                   && cleanupEx is not OutOfMemoryException
                                                   && cleanupEx is not StackOverflowException)
                    {
                        /* best-effort temp cleanup */
                    }

                    // Hardlink failed (likely cross-volume or unsupported filesystem)
                    var isCrossDevice = linkEx is IOException ioEx && ioEx.Message.Contains("error code 17");
                    if (!isCrossDevice)
                        isCrossDevice = linkEx is IOException ioEx2 && ioEx2.Message.Contains("error code 18"); // Unix EXDEV

                    if (isCrossDevice)
                        _logger.LogInformation("Hardlink not possible (source and destination are on different drives), falling back to copy: {Source} -> {Dest}", sourceFile, destFile);
                    else
                        _logger.LogWarning(linkEx, "Hardlink failed, falling back to copy: {Source} -> {Dest}", sourceFile, destFile);

                    // Fallback to copy — copy to a temp file first, then atomically rename onto destination
                    // so the existing file is never overwritten until a complete replacement is confirmed.
                    // Use Path.GetFileName to strip any separators from GetRandomFileName (satisfies static analysis).
                    // Use Path.Join (not Path.Combine) to prevent rooted second arg from silently discarding destDir.
                    var tempCopyName = Path.GetFileName(Path.GetRandomFileName()) + ".tmp";
                    var tempCopyPath = Path.Join(destDir, tempCopyName);
                    try
                    {
                        File.Copy(sourceFile, tempCopyPath, overwrite: true);
                        File.Move(tempCopyPath, destFile, overwrite: true);
                        LogMutation(FileMutationOutcome.Success, FileAction.HardlinkCopy, sourceFile, destFile, "Copied after hardlink failure");
                        return true;
                    }
                    finally
                    {
                        // Best-effort cleanup of temp copy if something went wrong before/after the move
                        try { if (File.Exists(tempCopyPath)) File.Delete(tempCopyPath); }
                        catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException
                                                       && cleanupEx is not OutOfMemoryException
                                                       && cleanupEx is not StackOverflowException)
                        {
                            // best-effort cleanup; ignore non-critical failures
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Hardlink/Copy failed: {Source} -> {Dest}", sourceFile, destFile);
                return false;
            }
        }

        private void CopyDirRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetRelativePath(src, dir);
                if (!FileUtils.TryResolveRelativePathWithinBase(dst, relative, out var sub))
                {
                    throw new IOException($"Directory copy destination escaped root: {relative}");
                }

                CopyDirRecursive(dir, sub);
            }

            foreach (var file in Directory.GetFiles(src, "*.*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetRelativePath(src, file);
                if (!FileUtils.TryResolveRelativePathWithinBase(dst, relative, out var destFile))
                {
                    throw new IOException($"File copy destination escaped root: {relative}");
                }

                if (File.Exists(destFile) && FileSystemSafety.FilesHaveSameContentAsync(file, destFile).GetAwaiter().GetResult())
                {
                    LogMutation(FileMutationOutcome.Skipped, FileAction.Copy, file, destFile, "Destination already has identical content");
                    continue;
                }

                File.Copy(file, destFile, true);
                LogMutation(FileMutationOutcome.Success, FileAction.Copy, file, destFile);
            }
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }

        private static ProcessStartInfo CreateRobocopyStartInfo(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "robocopy",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)))
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        public async Task<bool> PerformActionOn(FileAction action, string source, string? destination = null)
        {
            if (action == FileAction.None || destination == null) return true;

            // Ensure destination directory exists
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                switch (action)
                {
                    case FileAction.Move:
                        if (await MoveFileAsync(source, destination))
                        {
                            var sourceDirectory = Path.GetDirectoryName(source);
                            if (sourceDirectory != null)
                            {
                                FileSystemSafety.DeleteEmptyDirectories(sourceDirectory);
                            }
                            return true;
                        }
                        return false;
                    case FileAction.HardlinkCopy:
                        return await HardlinkFileAsync(source, destination);
                    case FileAction.Copy:
                        return await CopyFileAsync(source, destination);
                }

                // Unhandled action: We are unable to fulfill the request
                return false;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                LogMutation(FileMutationOutcome.Failed, action, source, destination, exception.Message);
                throw new InvalidOperationException($"Unable to perform {action} on {source} to {destination}", exception);
            }
        }

        private async Task<bool> TryCompleteIdempotentFileMoveAsync(string sourceFile, string destFile)
        {
            if (!File.Exists(sourceFile) || !File.Exists(destFile))
            {
                return false;
            }

            if (!await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, destFile))
            {
                return false;
            }

            try
            {
                File.Delete(sourceFile);
                LogMutation(FileMutationOutcome.Skipped, FileAction.Move, sourceFile, destFile, "Destination already has identical content; source removed");
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to remove idempotent move source {Source}", LogRedaction.SanitizeFilePath(sourceFile));
                return false;
            }
        }

        private async Task<bool> TrySkipSameContentAsync(FileAction action, string sourceFile, string destFile)
        {
            if (!File.Exists(sourceFile) || !File.Exists(destFile))
            {
                return false;
            }

            if (!await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, destFile))
            {
                return false;
            }

            LogMutation(FileMutationOutcome.Skipped, action, sourceFile, destFile, "Destination already has identical content");
            return true;
        }

        private void LogMutation(FileMutationOutcome outcome, FileAction action, string source, string? destination, string? reason = null)
        {
            var result = new FileMutationResult(outcome, action, source, destination, reason);
            _logger.LogInformation(
                "File mutation {Outcome}: {Action} {Source} -> {Destination}. Reason: {Reason}",
                result.Outcome,
                result.Action,
                LogRedaction.SanitizeFilePath(result.SourcePath),
                LogRedaction.SanitizeFilePath(result.DestinationPath ?? string.Empty),
                LogRedaction.SanitizeText(result.Reason ?? string.Empty));
        }
    }
}
