/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    public partial class FileMover
    {
        public Task<bool> MoveFileAsync(string sourceFile, string destFile) =>
            MoveFileAsync(sourceFile, destFile, operationId: null);

        public async Task<bool> MoveFilePreservingPhysicalIdentityAsync(
            string source,
            string destination,
            string expectedSourcePhysicalObjectIdentity,
            Guid? operationId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                expectedSourcePhysicalObjectIdentity);
            if (string.Equals(
                    Path.GetFullPath(source),
                    Path.GetFullPath(destination),
                    StringComparison.Ordinal))
            {
                try
                {
                    using var lease = PinnedAudiobookFileRegistrationLease.Open(
                        source,
                        expectedSourcePhysicalObjectIdentity);
                    return lease.MatchesCurrentPublication();
                }
                catch (Exception exception) when (exception is not (
                    OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    _logger.LogWarning(
                        exception,
                        "Blocked generation-preserving file move because the source identity is unavailable: {Source}",
                        LogRedaction.SanitizeFilePath(source));
                    return false;
                }
            }

            var markerlessResult =
                await TryMoveFilePreservingPhysicalIdentityMarkerlessAsync(
                    source,
                    destination,
                    expectedSourcePhysicalObjectIdentity,
                    operationId);
            if (markerlessResult.HasValue)
            {
                return markerlessResult.Value;
            }

            using var pathLock = await TryAcquireFileMoveGateAsync(
                source,
                destination);
            if (pathLock == null)
            {
                return false;
            }

            var recoveryOutcome = await TryRecoverInterruptedFileMoveClaimsAsync(
                pathLock,
                operationId);
            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.Completed)
            {
                using var recoveredDestination =
                    pathLock.DestinationParent.TryOpenExistingFile(
                        pathLock.DestinationName,
                        requireDeleteAccess: false);
                return recoveredDestination != null
                    && recoveredDestination.VisiblePathMatches()
                    && string.Equals(
                        recoveredDestination.GetObjectIdentity(),
                        expectedSourcePhysicalObjectIdentity,
                        StringComparison.Ordinal);
            }

            if (recoveryOutcome is FileMoveClaimRecoveryOutcome.Blocked
                or FileMoveClaimRecoveryOutcome.SourceRecreated)
            {
                return false;
            }

            var moved = await MoveFileWithLocksAsync(
                pathLock,
                operationId,
                expectedSourcePhysicalObjectIdentity,
                requirePhysicalIdentityPreservation: true);
            if (!moved)
            {
                return false;
            }

            using var destinationEntry =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            return destinationEntry != null
                && destinationEntry.VisiblePathMatches()
                && string.Equals(
                    destinationEntry.GetObjectIdentity(),
                    expectedSourcePhysicalObjectIdentity,
                    StringComparison.Ordinal);
        }

        internal async Task<bool> MoveFileAsync(
            string sourceFile,
            string destFile,
            Guid? operationId)
        {
            if (string.Equals(
                    Path.GetFullPath(sourceFile),
                    Path.GetFullPath(destFile),
                    StringComparison.Ordinal))
            {
                return true;
            }

            var markerlessResult = await TryMoveFileMarkerlessAsync(
                sourceFile,
                destFile,
                operationId);
            if (markerlessResult.HasValue)
            {
                return markerlessResult.Value;
            }

            using var pathLock = await TryAcquireFileMoveGateAsync(
                sourceFile,
                destFile);
            if (pathLock == null)
            {
                return false;
            }

            var recoveryOutcome = await TryRecoverInterruptedFileMoveClaimsAsync(
                pathLock,
                operationId);
            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.Completed)
            {
                return true;
            }

            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.Blocked)
            {
                return false;
            }
            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.SourceRecreated)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "A new source generation exists beside committed move state");
                return false;
            }

            return await MoveFileWithLocksAsync(
                pathLock,
                operationId);
        }

        private async Task<bool> MoveFileWithLocksAsync(
            FileMoveGateLease lease,
            Guid? operationId,
            string? expectedSourcePhysicalObjectIdentity = null,
            bool requirePhysicalIdentityPreservation = false)
        {
            var sourceFile = lease.SourcePath;
            var destFile = lease.DestinationPath;
            if (!string.IsNullOrWhiteSpace(expectedSourcePhysicalObjectIdentity))
            {
                using var sourceEntry = lease.SourceParent.TryOpenExistingFile(
                    lease.SourceName,
                    requireDeleteAccess: false);
                using var existingDestination = requirePhysicalIdentityPreservation
                    ? lease.DestinationParent.TryOpenExistingFile(
                        lease.DestinationName,
                        requireDeleteAccess: false)
                    : null;
                if (sourceEntry == null
                    || !sourceEntry.VisiblePathMatches()
                    || !string.Equals(
                        sourceEntry.GetObjectIdentity(),
                        expectedSourcePhysicalObjectIdentity,
                        StringComparison.Ordinal)
                    || (requirePhysicalIdentityPreservation
                        && (existingDestination != null
                            || DisableNativeFileRenameForTest
                            || !sourceEntry.IsOnSameVolume(
                                lease.DestinationParent))))
                {
                    return false;
                }
            }

            var pathEquivalence = await TryDetermineFilesystemPathEquivalenceAsync(
                sourceFile,
                destFile);
            if (pathEquivalence == true)
            {
                LogMutation(
                    FileMutationOutcome.Skipped,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source and destination identify the same file");
                return true;
            }

            var idempotentOutcome = requirePhysicalIdentityPreservation
                ? IdempotentFileMoveOutcome.NotApplicable
                : await TryCompleteIdempotentFileMoveAsync(
                    lease,
                    operationId);
            if (idempotentOutcome == IdempotentFileMoveOutcome.Completed)
            {
                return true;
            }

            if (idempotentOutcome == IdempotentFileMoveOutcome.SourcePathRecreated)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source path was recreated while completing an idempotent move");
                return false;
            }

            if (pathEquivalence == null
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(destFile))
            {
                _logger.LogWarning(
                    "Blocked copy-and-delete file fallback because filesystem identity or link safety could not prove distinct regular files: {Source} -> {Destination}",
                    LogRedaction.SanitizeFilePath(sourceFile),
                    LogRedaction.SanitizeFilePath(destFile));
                return false;
            }

            var managedFallback = await TryManagedFileMoveFallbackAsync(
                lease,
                operationId,
                expectedSourcePhysicalObjectIdentity,
                requirePhysicalIdentityPreservation);
            if (managedFallback == FileMoveFallbackOutcome.Success)
            {
                LogMutation(
                    FileMutationOutcome.Success,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Verified copy fallback");
                return true;
            }

            if (managedFallback == FileMoveFallbackOutcome.SourceRetained)
            {
                LogMutation(
                    FileMutationOutcome.Failed,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Destination was published but the verified source could not be removed");
                return false;
            }

            LogMutation(
                FileMutationOutcome.Failed,
                FileAction.Move,
                sourceFile,
                destFile,
                "No verified anchored file move fallback completed");
            return false;
        }

    }
}
