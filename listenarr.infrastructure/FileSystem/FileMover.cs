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
using System.Diagnostics.CodeAnalysis;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

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

    }
}
