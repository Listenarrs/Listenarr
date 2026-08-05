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
        private readonly IFileSystemSemanticsResolver _semanticsResolver;

        internal Func<Task>? AfterSourceStateCreatedForTestAsync { get; init; }
        internal Func<string, string, Task>? AfterSourceQuarantinedForTestAsync { get; init; }
        internal Func<Task>? AfterDestinationStateCreatedForTestAsync { get; init; }
        internal Func<string, string, Task>? AfterDestinationQuarantinedForTestAsync { get; init; }
        internal Func<Task>? AfterSourceRetirementCommittedForTestAsync { get; init; }
        internal Func<string, Task>? AfterDestinationPublishedForTestAsync { get; init; }
        internal Func<Task>? AfterSourceClaimDeletedForTestAsync { get; init; }
        internal Func<Task>? AfterFileMoveStateCleanedForTestAsync { get; init; }
        internal Func<Task>? AfterPreparedDestinationCapturedForTestAsync { get; init; }
        internal Func<Task>? AfterPreparedClaimMovedBeforeEvidenceForTestAsync { get; init; }
        internal Func<Task>? AfterPreparedClaimPublishedForTestAsync { get; init; }
        internal Func<Task>? AfterPreparedPublicationCommittedForTestAsync { get; init; }
        internal Func<Task>? AfterPreparedDestinationPublishedForTestAsync { get; init; }
        internal Func<Task>? AfterDirectoryCopyPreflightForTestAsync { get; init; }
        internal Action? BeforeDirectoryTreePreflightForTest { get; init; }

        public FileMover(
            ILogger<FileMover> logger,
            IProcessRunner? processRunner = null,
            IOptions<FileMoverOptions>? options = null,
            IFileSystemSemanticsResolver? semanticsResolver = null)
        {
            _logger = logger;
            _ = processRunner;
            _ = options;
            _semanticsResolver = semanticsResolver ?? new FileSystemSemanticsResolver();
        }

        public async Task<bool> MoveDirectoryAsync(string sourceDir, string destDir)
        {
            var recoveredRename = TryRecoverPinnedDirectoryRename(
                sourceDir,
                destDir);
            switch (recoveredRename)
            {
                case PinnedDirectoryMoveOutcome.Moved:
                    return true;
                case PinnedDirectoryMoveOutcome.NotMoved:
                case PinnedDirectoryMoveOutcome.NotApplicable:
                case null:
                    break;
                case PinnedDirectoryMoveOutcome.Indeterminate:
                    throw new IOException(
                        "An interrupted directory rename could not be reconciled safely.");
                default:
                    throw new InvalidOperationException(
                        $"Unsupported recovered directory move outcome: {recoveredRename}.");
            }

            var pathEquivalence = await TryDetermineFilesystemPathEquivalenceAsync(
                sourceDir,
                destDir);
            if (pathEquivalence == true)
            {
                LogMutation(
                    FileMutationOutcome.Skipped,
                    FileAction.Move,
                    sourceDir,
                    destDir,
                    "Source and destination identify the same directory");
                return true;
            }

            var pathsOverlap = await TryDetermineDirectoryOverlapAsync(sourceDir, destDir);
            if (pathsOverlap == true)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceDir,
                    destDir,
                    "Source and destination directories overlap");
                return false;
            }

            if (!TryRecoverInterruptedCopiedSourceCleanup(
                    sourceDir,
                    out var recoveryReason))
            {
                _logger.LogWarning(
                    "Blocked directory move because interrupted source cleanup could not be recovered: {Reason}",
                    recoveryReason);
                return false;
            }

            // Public directory pathnames cannot be bound to the directory object
            // across a managed Directory.Move call. Always use the verified
            // snapshot/copy/cleanup protocol below.
            var forceVerifiedFallback = false;
            try
            {
                BeforeDirectoryMoveAttemptForTest?.Invoke();
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                forceVerifiedFallback = true;
                _logger.LogDebug(
                    exception,
                    "The direct-directory-move test boundary requested the verified fallback.");
            }

            if (pathEquivalence == null || pathsOverlap != false)
            {
                _logger.LogWarning(
                    "Blocked copy-and-delete directory fallback because filesystem identity could not prove distinct, non-overlapping paths: {Source} -> {Destination}",
                    LogRedaction.SanitizeFilePath(sourceDir),
                    LogRedaction.SanitizeFilePath(destDir));
                return false;
            }

            if (!forceVerifiedFallback)
            {
                var nativeMove = TryPinnedSameVolumeDirectoryMove(
                    sourceDir,
                    destDir);
                switch (nativeMove)
                {
                    case PinnedDirectoryMoveOutcome.Moved:
                        return true;
                    case PinnedDirectoryMoveOutcome.NotMoved:
                        return false;
                    case PinnedDirectoryMoveOutcome.Indeterminate:
                        throw new IOException(
                            "The directory rename may have completed, but its final filesystem state could not be reconciled safely.");
                    case PinnedDirectoryMoveOutcome.NotApplicable:
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported pinned directory move outcome: {nativeMove}.");
                }
            }

            if (!TryCaptureDirectoryCopySnapshot(
                    sourceDir,
                    out var copySnapshot,
                    out var sourceTraversalReason)
                || copySnapshot == null
                || (Directory.Exists(destDir)
                    && !FileSystemSafety.TryEnumerateTreeWithoutLinks(
                        destDir,
                        out _,
                        out _,
                        out sourceTraversalReason)))
            {
                _logger.LogWarning(
                    "Blocked copy-and-delete directory fallback because a filesystem tree could not be traversed safely: {Reason}",
                    sourceTraversalReason);
                return false;
            }

            if (!forceVerifiedFallback
                && !CanCopyDirectoryAcrossVolumesWithoutFidelityLoss(copySnapshot))
            {
                _logger.LogWarning(
                    "Blocked cross-volume directory move because hardlinks or extended metadata cannot be reproduced without fidelity loss: {Source} -> {Destination}",
                    LogRedaction.SanitizeFilePath(sourceDir),
                    LogRedaction.SanitizeFilePath(destDir));
                return false;
            }

            var destinationRoot = Path.GetFullPath(destDir);

            // Fallback to copy plus verified, non-recursive source cleanup. New or
            // changed source content is preserved instead of being recursively deleted.
            try
            {
                await CopyDirectorySnapshotAsync(copySnapshot, destinationRoot);

                var cleanup = await CleanupCopiedSourceTreeAsync(
                    copySnapshot,
                    destinationRoot);
                if (!cleanup.DestinationVerified)
                {
                    _logger.LogWarning(
                        "Directory copy fallback preserved the source because destination verification failed: {Reason}",
                        cleanup.Reason);
                    return false;
                }

                if (!cleanup.SourceRemoved)
                {
                    _logger.LogWarning(
                        "Directory move copied the destination but preserved changed source content at {Source}: {Reason}",
                        LogRedaction.SanitizeFilePath(sourceDir),
                        cleanup.Reason);
                    LogMutation(
                        FileMutationOutcome.Failed,
                        FileAction.Move,
                        sourceDir,
                        destDir,
                        cleanup.Reason);
                    return false;
                }

                LogMutation(FileMutationOutcome.Success, FileAction.Move, sourceDir, destDir);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(
                    ex,
                    "Verified copy+cleanup failed for directory {Source} -> {Dest}; preserving the source because an external path-based fallback cannot retain pinned filesystem-generation authority",
                    sourceDir,
                    destDir);
                return false;
            }
        }

    }
}
