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
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Application.Security;

namespace Listenarr.Infrastructure.FileSystem
{
    public partial class FileMover(
        ILogger<FileMover> logger,
        IProcessRunner processRunner,
        FileMoverOptions options) : IFileMover
    {
        // .NET 8 has no managed BCL equivalent for hardlink creation.
        // LibraryImport (source-generated P/Invoke, .NET 7+) is used instead of the legacy
        // DllImport attribute to minimise unmanaged interop overhead and satisfy CA1060/CA2101.
        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "No managed BCL equivalent for hardlink creation exists in .NET 8.")]
        private static partial bool CreateHardLinkNative(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "No managed BCL equivalent for hardlink creation exists in .NET 8.")]
        private static partial int LinkNative(string oldpath, string newpath);

        public async Task<bool> MoveDirectoryAsync(string sourceDir, string destDir)
        {
            if (FileUtils.IsSameDirectory(destDir, sourceDir))
            {
                return true;
            }

            if (FileUtils.IsPathInsideOf(destDir, sourceDir))
            {
                logger.LogError($"Cannot move a directory inside itslef from {sourceDir} to {destDir}");
                return false;
            }

            // Try move with retries
            var attempt = 0;
            var delay = 1000;

            for (; attempt < options.MaxRetries; attempt++)
            {
                try
                {
                    Directory.Move(sourceDir, destDir);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(ex, "Directory.Move attempt {Attempt} failed: {Source} -> {Dest}", attempt + 1, sourceDir, destDir);
                    try
                    {
                        var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
                        logger.LogWarning("Directory listing sample: {Sample}", string.Join(", ", files.Take(5).Select(f => Path.GetFileName(f))));
                    }
                    catch (Exception diagEx) when (diagEx is not OperationCanceledException && diagEx is not OutOfMemoryException && diagEx is not StackOverflowException)
                    {
                        logger.LogDebug(diagEx, "Failed to collect directory listing diagnostics for {Source}", sourceDir);
                    }

                    try
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var dirSec = new DirectoryInfo(sourceDir).GetAccessControl();
                            var owner = dirSec.GetOwner(typeof(NTAccount))?.ToString() ?? "unknown";
                            logger.LogWarning("Directory owner: {Owner}", owner);
                        }
                    }
                    catch (Exception ownerEx) when (ownerEx is not OperationCanceledException && ownerEx is not OutOfMemoryException && ownerEx is not StackOverflowException)
                    {
                        logger.LogDebug(ownerEx, "Failed to resolve directory owner diagnostics for {Source}", sourceDir);
                    }

                    if (attempt < options.MaxRetries - 1)
                    {
                        await Task.Delay(Math.Min(delay, options.MaxBackoffMs));
                        delay = Math.Min(delay * 2, options.MaxBackoffMs);
                    }
                }
            }

            // Fallback to copy+delete
            try
            {
                CopyDirRecursive(sourceDir, destDir);
                try { Directory.Delete(sourceDir, true); }
                catch (Exception deleteEx) when (deleteEx is not OperationCanceledException && deleteEx is not OutOfMemoryException && deleteEx is not StackOverflowException)
                {
                    logger.LogDebug(deleteEx, "Failed deleting source directory after copy fallback for {Source}", sourceDir);
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Copy+delete fallback failed for directory {Source} -> {Dest}", sourceDir, destDir);

                return await MoveWithRobocopy(sourceDir, destDir, "*.*");
            }
        }

        public async Task<bool> MoveFileAsync(string sourceFile, string destFile)
        {
            var attempt = 0;
            var delay = 1000;

            for (; attempt < options.MaxRetries; attempt++)
            {
                try
                {
                    File.Move(sourceFile, destFile, true);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(ex, "File.Move attempt {Attempt} failed: {Source} -> {Dest}", attempt + 1, sourceFile, destFile);
                    try
                    {
                        using var stream = File.Open(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                        logger.LogDebug("Able to open source file for read during diagnostic: {File}", sourceFile);
                    }
                    catch (Exception diagEx) when (diagEx is not OperationCanceledException && diagEx is not OutOfMemoryException && diagEx is not StackOverflowException)
                    {
                        logger.LogDebug(diagEx, "Failed to collect file diagnostics for {Source}", sourceFile);
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        try
                        {
                            var fileSec = new FileInfo(sourceFile).GetAccessControl();
                            var owner = fileSec.GetOwner(typeof(NTAccount))?.ToString() ?? "unknown";
                            logger.LogWarning("File owner for {File}: {Owner}", sourceFile, owner);
                        }
                        catch (Exception ownerEx) when (ownerEx is not OperationCanceledException && ownerEx is not OutOfMemoryException && ownerEx is not StackOverflowException)
                        {
                            logger.LogDebug(ownerEx, "Failed to resolve file owner diagnostics for {Source}", sourceFile);
                        }
                    }

                    if (attempt < options.MaxRetries - 1)
                    {
                        await Task.Delay(Math.Min(delay, options.MaxBackoffMs));
                        delay = Math.Min(delay * 2, options.MaxBackoffMs);
                    }
                }
            }

            // Fallback copy+delete
            try
            {
                File.Copy(sourceFile, destFile, true);
                try { File.Delete(sourceFile); }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogDebug(exception, "Failed deleting source file after copy fallback for {Source}", sourceFile);
                }
                return true;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(exception, "Copy+delete fallback failed for file {Source} -> {Dest}", sourceFile, destFile);

                var sourceDirectory = Path.GetDirectoryName(sourceFile) ?? string.Empty;
                var destinationDirectory = Path.GetDirectoryName(destFile) ?? string.Empty;

                return await MoveWithRobocopy(sourceDirectory, destinationDirectory, Path.GetFileName(sourceFile));
            }
        }

        public async Task<bool> CopyFileAsync(string sourceFile, string destFile)
        {
            try
            {
                File.Copy(sourceFile, destFile, true);
                return true;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(exception, $"Copy file failed: {sourceFile} -> {destFile}");
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
                    logger.LogInformation("Hardlinked file: {Source} -> {Dest}", sourceFile, destFile);
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
                        logger.LogInformation("Hardlink not possible (source and destination are on different drives), falling back to copy: {Source} -> {Dest}", sourceFile, destFile);
                    else
                        logger.LogWarning(linkEx, "Hardlink failed, falling back to copy: {Source} -> {Dest}", sourceFile, destFile);

                    // Copy fallback
                    if (await CopyFileAsync(sourceFile, destFile))
                    {
                        return true;
                    }

                    logger.LogError("Hardlink/Copy failed: {Source} -> {Dest}", sourceFile, destFile);
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(exception, "Hardlink/Copy failed: {Source} -> {Dest}", sourceFile, destFile);
            }

            return false;
        }

        internal void CopyDirRecursive(string src, string dst)
        {
            src = FileUtils.NormalizeStoredPath(src);
            src = FileUtils.EnsureTrailingSeparator(src);

            dst = FileUtils.NormalizeStoredPath(dst);
            dst = FileUtils.EnsureTrailingSeparator(dst);

            if (dst.Equals(src, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CopyDirRecursiveIteration(src, dst, dst);
        }

        private void CopyDirRecursiveIteration(string src, string dst, string originalDst)
        {
            src = FileUtils.NormalizeStoredPath(src);
            src = FileUtils.EnsureTrailingSeparator(src);

            if (src.Equals(originalDst, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(dst);
            foreach (var sourceSubdirectory in Directory.GetDirectories(src, "*", SearchOption.TopDirectoryOnly))
            {
                var destinationSubdirectory = Path.Join(dst, Path.GetFileName(sourceSubdirectory));
                CopyDirRecursiveIteration(sourceSubdirectory, destinationSubdirectory, originalDst);
            }

            foreach (var sourceFile in Directory.GetFiles(src, "*.*", SearchOption.TopDirectoryOnly))
            {
                var destinationFile = Path.Join(dst, Path.GetFileName(sourceFile));
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
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
                                FileUtils.DeleteEmptyDirectories(sourceDirectory);
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
                throw new InvalidOperationException($"Unable to perform {action} on {source} to {destination}", exception);
            }
        }

        private async Task<bool> MoveWithRobocopy(string sourceDirectory, string desintationDirectory, string filename)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !options.EnableRobocopy)
            {
                return false;
            }

            try
            {
                logger.LogInformation($"Attempting robocopy for move: {sourceDirectory} -> {desintationDirectory}");
                var startInfo = CreateRobocopyStartInfo(
                    sourceDirectory,
                    desintationDirectory,
                    filename,
                    "/MOV",
                    "/E",
                    "/NFL",
                    "/NDL",
                    "/NJH",
                    "/NJS",
                    "/NP");

                var pr = await processRunner.RunAsync(startInfo, options.RobocopyTimeoutMs);
                if (!pr.TimedOut && pr.ExitCode == 1)
                {
                    logger.LogInformation($"Robocopy fallback succeeded with exit code {pr.ExitCode}");
                    logger.LogDebug("Robocopy stdout: {Out}", LogRedaction.RedactText(StringUtils.Truncate(pr.Stdout, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return true;
                }

                logger.LogWarning("Robocopy fallback failed or returned non-success code: {Code}. Stderr: {Err}", pr.ExitCode, LogRedaction.RedactText(StringUtils.Truncate(pr.Stderr, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(exception, "Robocopy fallback threw an exception");
            }

            return false;
        }
    }
}


