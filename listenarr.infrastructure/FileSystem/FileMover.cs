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
using Microsoft.Extensions.Options;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Application.Security;

namespace Listenarr.Infrastructure.FileSystem
{
    internal enum FileMutationOutcome
    {
        Success,
        Skipped,
        Blocked,
        Failed
    }

    internal sealed record FileMutationResult(
        FileMutationOutcome Outcome,
        FileAction Action,
        string SourcePath,
        string? DestinationPath,
        string? Reason = null);

    public partial class FileMover : IFileMover
    {
        // .NET has no managed BCL equivalent for hardlink creation.
        // LibraryImport (source-generated P/Invoke, .NET 7+) is used instead of the legacy
        // DllImport attribute to minimise unmanaged interop overhead and satisfy CA1060/CA2101.
        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "No managed BCL equivalent for hardlink creation exists in .NET.")]
        private static partial bool CreateHardLinkNative(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "No managed BCL equivalent for hardlink creation exists in .NET.")]
        private static partial int LinkNative(string oldpath, string newpath);

        private readonly ILogger<FileMover> _logger;
        private readonly IProcessRunner? _processRunner;
        private readonly FileMoverOptions _options;

        public FileMover(ILogger<FileMover> logger, IProcessRunner? processRunner = null, IOptions<FileMoverOptions>? options = null)
        {
            _logger = logger;
            _processRunner = processRunner;
            _options = options?.Value ?? new FileMoverOptions();
        }

        public async Task<bool> MoveDirectoryAsync(string sourceDir, string destDir)
        {
            // Try move with retries
            var attempt = 0;
            var delay = 1000;

            for (; attempt < _options.MaxRetries; attempt++)
            {
                try
                {
                    Directory.Move(sourceDir, destDir);
                    LogMutation(FileMutationOutcome.Success, FileAction.Move, sourceDir, destDir);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Directory.Move attempt {Attempt} failed: {Source} -> {Dest}", attempt + 1, sourceDir, destDir);
                    try
                    {
                        var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
                        _logger.LogWarning("Directory listing sample: {Sample}", string.Join(", ", files.Take(5).Select(f => Path.GetFileName(f))));
                    }
                    catch (Exception diagEx) when (diagEx is not OperationCanceledException && diagEx is not OutOfMemoryException && diagEx is not StackOverflowException)
                    {
                        _logger.LogDebug(diagEx, "Failed to collect directory listing diagnostics for {Source}", sourceDir);
                    }

                    try
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var dirSec = new DirectoryInfo(sourceDir).GetAccessControl();
                            var owner = dirSec.GetOwner(typeof(NTAccount))?.ToString() ?? "unknown";
                            _logger.LogWarning("Directory owner: {Owner}", owner);
                        }
                    }
                    catch (Exception ownerEx) when (ownerEx is not OperationCanceledException && ownerEx is not OutOfMemoryException && ownerEx is not StackOverflowException)
                    {
                        _logger.LogDebug(ownerEx, "Failed to resolve directory owner diagnostics for {Source}", sourceDir);
                    }

                    if (attempt < _options.MaxRetries - 1)
                    {
                        await Task.Delay(Math.Min(delay, _options.MaxBackoffMs));
                        delay = Math.Min(delay * 2, _options.MaxBackoffMs);
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
                    _logger.LogDebug(deleteEx, "Failed deleting source directory after copy fallback for {Source}", sourceDir);
                }
                LogMutation(FileMutationOutcome.Success, FileAction.Move, sourceDir, destDir);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Copy+delete fallback failed for directory {Source} -> {Dest}", sourceDir, destDir);

                // On Windows attempt robocopy as a final-resort atomic-ish fallback
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _options.EnableRobocopy && _processRunner != null)
                    {
                        _logger.LogWarning("Attempting robocopy fallback for directory move: {Source} -> {Dest}", sourceDir, destDir);
                        var startInfo = CreateRobocopyStartInfo(
                            sourceDir,
                            destDir,
                            "/MOVE",
                            "/E",
                            "/NFL",
                            "/NDL",
                            "/NJH",
                            "/NJS",
                            "/NP");

                        var pr = await _processRunner.RunAsync(startInfo, _options.RobocopyTimeoutMs);
                        if (!pr.TimedOut && pr.ExitCode <= 7 && pr.ExitCode >= 0)
                        {
                            _logger.LogInformation("Robocopy fallback succeeded with exit code {Code}", pr.ExitCode);
                            _logger.LogDebug("Robocopy stdout: {Out}", LogRedaction.RedactText(Truncate(pr.Stdout, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
                            return true;
                        }

                        _logger.LogWarning("Robocopy fallback failed or returned non-success code: {Code}. Stderr: {Err}", pr.ExitCode, LogRedaction.RedactText(Truncate(pr.Stderr, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
                    }
                }
                catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException)
                {
                    _logger.LogWarning(rex, "Robocopy fallback threw an exception");
                }

                return false;
            }
        }

        public async Task<bool> MoveFileAsync(string sourceFile, string destFile)
        {
            var attempt = 0;
            var delay = 1000;

            if (await TryCompleteIdempotentFileMoveAsync(sourceFile, destFile))
            {
                return true;
            }

            for (; attempt < _options.MaxRetries; attempt++)
            {
                try
                {
                    File.Move(sourceFile, destFile, true);
                    LogMutation(FileMutationOutcome.Success, FileAction.Move, sourceFile, destFile);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "File.Move attempt {Attempt} failed: {Source} -> {Dest}", attempt + 1, sourceFile, destFile);
                    try
                    {
                        using var stream = File.Open(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                        _logger.LogDebug("Able to open source file for read during diagnostic: {File}", sourceFile);
                    }
                    catch (Exception diagEx) when (diagEx is not OperationCanceledException && diagEx is not OutOfMemoryException && diagEx is not StackOverflowException)
                    {
                        _logger.LogDebug(diagEx, "Failed to collect file diagnostics for {Source}", sourceFile);
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        try
                        {
                            var fileSec = new FileInfo(sourceFile).GetAccessControl();
                            var owner = fileSec.GetOwner(typeof(NTAccount))?.ToString() ?? "unknown";
                            _logger.LogWarning("File owner for {File}: {Owner}", sourceFile, owner);
                        }
                        catch (Exception ownerEx) when (ownerEx is not OperationCanceledException && ownerEx is not OutOfMemoryException && ownerEx is not StackOverflowException)
                        {
                            _logger.LogDebug(ownerEx, "Failed to resolve file owner diagnostics for {Source}", sourceFile);
                        }
                    }

                    if (attempt < _options.MaxRetries - 1)
                    {
                        await Task.Delay(Math.Min(delay, _options.MaxBackoffMs));
                        delay = Math.Min(delay * 2, _options.MaxBackoffMs);
                    }
                }
            }

            // Fallback copy+delete
            try
            {
                File.Copy(sourceFile, destFile, true);
                try { File.Delete(sourceFile); }
                catch (Exception deleteEx) when (deleteEx is not OperationCanceledException && deleteEx is not OutOfMemoryException && deleteEx is not StackOverflowException)
                {
                    _logger.LogDebug(deleteEx, "Failed deleting source file after copy fallback for {Source}", sourceFile);
                }
                LogMutation(FileMutationOutcome.Success, FileAction.Move, sourceFile, destFile);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Copy+delete fallback failed for file {Source} -> {Dest}", sourceFile, destFile);

                // On Windows attempt robocopy for single-file move as a last resort
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _options.EnableRobocopy && _processRunner != null)
                    {
                        _logger.LogWarning("Attempting robocopy fallback for file move: {Source} -> {Dest}", sourceFile, destFile);
                        var srcDir = Path.GetDirectoryName(sourceFile) ?? string.Empty;
                        var dstDir = Path.GetDirectoryName(destFile) ?? string.Empty;
                        var fileName = Path.GetFileName(sourceFile);
                        var startInfo = CreateRobocopyStartInfo(
                            srcDir,
                            dstDir,
                            fileName,
                            "/MOV",
                            "/E",
                            "/NFL",
                            "/NDL",
                            "/NJH",
                            "/NJS",
                            "/NP");

                        var pr = await _processRunner.RunAsync(startInfo, _options.RobocopyTimeoutMs);
                        if (!pr.TimedOut && pr.ExitCode == 1)
                        {
                            _logger.LogInformation("Robocopy fallback succeeded with exit code {Code}", pr.ExitCode);
                            _logger.LogDebug("Robocopy stdout: {Out}", LogRedaction.RedactText(Truncate(pr.Stdout, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
                            return true;
                        }

                        _logger.LogWarning("Robocopy fallback failed or returned non-success code: {Code}. Stderr: {Err}", pr.ExitCode, LogRedaction.RedactText(Truncate(pr.Stderr, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
                    }
                }
                catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException)
                {
                    _logger.LogWarning(rex, "Robocopy fallback threw an exception");
                }

                return false;
            }
        }

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

                if (File.Exists(destFile) && FileUtils.FilesHaveSameContentAsync(file, destFile).GetAwaiter().GetResult())
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

            if (!await FileUtils.FilesHaveSameContentAsync(sourceFile, destFile))
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

            if (!await FileUtils.FilesHaveSameContentAsync(sourceFile, destFile))
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
