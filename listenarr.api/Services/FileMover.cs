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
using System.Text.Json.Serialization;
using Listenarr.Domain.Utils;

namespace Listenarr.Api.Services
{
    public partial class FileMover : IFileMover
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

            for (; attempt < _options.MaxRetries; attempt++)
            {
                try
                {
                    File.Move(sourceFile, destFile, true);
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

        public Task<bool> CopyDirectoryAsync(string sourceDir, string destDir)
        {
            try
            {
                CopyDirRecursive(sourceDir, destDir);
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Copy directory failed: {Source} -> {Dest}", sourceDir, destDir);
                return Task.FromResult(false);
            }
        }

        public Task<bool> CopyFileAsync(string sourceFile, string destFile)
        {
            try
            {
                File.Copy(sourceFile, destFile, true);
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Copy file failed: {Source} -> {Dest}", sourceFile, destFile);
                return Task.FromResult(false);
            }
        }

        public Task<bool> HardlinkFileAsync(string sourceFile, string destFile)
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
                    _logger.LogInformation("Hardlinked file: {Source} -> {Dest}", sourceFile, destFile);
                    return Task.FromResult(true);
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
                        _logger.LogInformation("Copied file (hardlink fallback): {Source} -> {Dest}", sourceFile, destFile);
                        return Task.FromResult(true);
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
                return Task.FromResult(false);
            }
        }

        private void CopyDirRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.TopDirectoryOnly))
            {
                var sub = Path.Join(dst, Path.GetFileName(dir));
                CopyDirRecursive(dir, sub);
            }

            foreach (var file in Directory.GetFiles(src, "*.*", SearchOption.TopDirectoryOnly))
            {
                var destFile = Path.Join(dst, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }
        }

        private async Task<int?> RunRobocopyAsync(string sourceDir, string destDir, bool move = false, string? filePattern = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

            if (!_options.EnableRobocopy) return null;

            var args = new List<string> { sourceDir, destDir };

            if (!string.IsNullOrWhiteSpace(filePattern))
            {
                args.Add(filePattern);
            }

            // Use /MOV for files or /MOVE for directories to move and delete source
            if (move)
            {
                // For directories, /MOVE is appropriate; for file pattern present, /MOV
                args.Add(string.IsNullOrWhiteSpace(filePattern) ? "/MOVE" : "/MOV");
            }

            // Mirror recursion for directories
            args.AddRange(new[] { "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP" });

            var startInfo = CreateRobocopyStartInfo(args.ToArray());

            try
            {
                if (_processRunner != null)
                {
                    var pr = await _processRunner.RunAsync(startInfo, _options.RobocopyTimeoutMs);
                    if (pr.TimedOut)
                    {
                        _logger.LogWarning("Robocopy timed out after {Ms}ms. Stdout: {Out} Stderr: {Err}", _options.RobocopyTimeoutMs, LogRedaction.RedactText(Truncate(pr.Stdout, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()), LogRedaction.RedactText(Truncate(pr.Stderr, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
                        return null;
                    }

                    var exit = pr.ExitCode;
                    _logger.LogDebug("Robocopy exit code {Exit}. Stdout: {Out} Stderr: {Err}", exit, LogRedaction.RedactText(Truncate(pr.Stdout, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()), LogRedaction.RedactText(Truncate(pr.Stderr, 2000), LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return exit;
                }
                else
                {
                    _logger.LogWarning("Robocopy requested but no IProcessRunner is registered; skipping direct process start for safety.");
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Exception while running robocopy");
                return null;
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

        public enum FileAction
        {
            [JsonPropertyName("none")]
            None,
            [JsonPropertyName("move")]
            Move,
            [JsonPropertyName("copy")]
            Copy,
            [JsonPropertyName("hardlink/copy")]
            HardlinkCopy
        }

        public async Task PerformActionOn(FileAction action, string source, string? destination = null, HashSet<string>? usedDestinations = null)
        {
            if (action == FileAction.None || destination == null) return;

            if (usedDestinations == null)
            {
                usedDestinations = [];
            }

            // Ensure destination directory exists
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            destination = FileUtils.GetUniqueDestinationPath(destination, System.IO.File.Exists, usedDestinations);

            try
            {
                switch (action)
                {
                    case FileAction.Move:
                        System.IO.File.Move(source, destination, overwrite: false);
                        break;
                    case FileAction.HardlinkCopy:
                        if (!await HardlinkFileAsync(source, destination))
                            throw new IOException($"HardlinkFileAsync failed: {source} -> {destination}");
                        break;
                    case FileAction.Copy:
                        System.IO.File.Copy(source, destination, overwrite: false);
                        break;
                }

                usedDestinations.Add(destination);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException($"Unable to perform {action} on {source}", exception);
            }
        }
    }
}


