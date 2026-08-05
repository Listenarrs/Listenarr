/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    public partial class FileMover : IFileMover
    {
        public async Task<bool> CopyDirectoryAsync(string sourceDir, string destDir)
        {
            try
            {
                var pathEquivalence = await TryDetermineFilesystemPathEquivalenceAsync(
                    sourceDir,
                    destDir);
                if (pathEquivalence != false)
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        FileAction.Copy,
                        sourceDir,
                        destDir,
                        pathEquivalence == true
                            ? "Source and destination identify the same directory"
                            : "Filesystem identity could not prove distinct directories");
                    return false;
                }

                var pathsOverlap = await TryDetermineDirectoryOverlapAsync(sourceDir, destDir);
                if (pathsOverlap != false)
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        FileAction.Copy,
                        sourceDir,
                        destDir,
                        pathsOverlap == true
                            ? "Source and destination directories overlap"
                            : "Filesystem identity could not prove non-overlapping directories");
                    return false;
                }

                if (!TryRecoverInterruptedCopiedSourceCleanup(
                        sourceDir,
                        out var recoveryReason))
                {
                    _logger.LogWarning(
                        "Blocked directory copy because interrupted source cleanup could not be recovered: {Reason}",
                        recoveryReason);
                    return false;
                }

                if (IsLinkedOrUnverifiableEntry(sourceDir)
                    || (Directory.Exists(destDir) && IsLinkedOrUnverifiableEntry(destDir)))
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        FileAction.Copy,
                        sourceDir,
                        destDir,
                        "A directory endpoint could not be verified without links");
                    return false;
                }

                if (!TryCaptureDirectoryCopySnapshot(
                        sourceDir,
                        out var snapshot,
                        out var traversalReason)
                    || snapshot == null
                    || (Directory.Exists(destDir)
                        && !FileSystemSafety.TryEnumerateTreeWithoutLinks(
                            destDir,
                            out _,
                            out _,
                            out traversalReason)))
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        FileAction.Copy,
                        sourceDir,
                        destDir,
                        traversalReason);
                    return false;
                }

                if (AfterDirectoryCopyPreflightForTestAsync != null)
                {
                    await AfterDirectoryCopyPreflightForTestAsync();
                }

                await CopyDirectorySnapshotAsync(snapshot, destDir);
                LogMutation(FileMutationOutcome.Success, FileAction.Copy, sourceDir, destDir);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Copy directory failed: {Source} -> {Dest}", sourceDir, destDir);
                return false;
            }
        }

        public async Task<bool> CopyFileAsync(string sourceFile, string destFile)
        {
            return await CopyOrHardlinkPinnedFileAsync(
                FileAction.Copy,
                sourceFile,
                destFile,
                preferHardlink: false);
        }

        public async Task<bool> HardlinkFileAsync(string sourceFile, string destFile)
        {
            return await CopyOrHardlinkPinnedFileAsync(
                FileAction.HardlinkCopy,
                sourceFile,
                destFile,
                preferHardlink: true);
        }

        private async Task<bool> CopyOrHardlinkPinnedFileAsync(
            FileAction action,
            string sourceFile,
            string destFile,
            bool preferHardlink,
            Action<IAudiobookFileRegistrationLease>? capturePublication = null,
            Guid? registrationOperationId = null)
        {
            try
            {
                if (await IsSameFilesystemPathAsync(sourceFile, destFile))
                {
                    if (capturePublication != null)
                    {
                        CapturePublishedRegistrationLease(
                            PinnedAudiobookFileRegistrationLease.Open(destFile),
                            capturePublication);
                    }
                    LogMutation(
                        FileMutationOutcome.Skipped,
                        action,
                        sourceFile,
                        destFile,
                        "Source and destination identify the same file");
                    return true;
                }

                using var lease = await TryAcquireFileMoveGateAsync(
                    sourceFile,
                    destFile,
                    allowExistingAliasForRecovery: true);
                if (lease == null)
                {
                    return false;
                }

                var publicationStateName =
                    await GetPreparedFilePublicationStateNameAsync(destFile);
                var recovery = await RecoverPreparedFilePublicationAsync(
                    lease.DestinationParent,
                    lease.DestinationName,
                    publicationStateName);
                if (recovery.Outcome
                    == PreparedPublicationRecoveryOutcome.Completed)
                {
                    return TryCompleteRecoveredPreparedPublication(
                        recovery,
                        lease,
                        action,
                        sourceFile,
                        destFile,
                        capturePublication);
                }

                if (await IsFilesystemAliasAsync(sourceFile, destFile))
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        action,
                        sourceFile,
                        destFile,
                        "Source and destination are linked aliases of the same file");
                    return false;
                }

                if (!TryValidateUnlinkedFileOperationEndpoints(
                        sourceFile,
                        destFile,
                        out var endpointReason))
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        action,
                        sourceFile,
                        destFile,
                        endpointReason);
                    return false;
                }

                if (BeforeFileSameContentShortcutForTestAsync != null)
                {
                    await BeforeFileSameContentShortcutForTestAsync(
                        action,
                        sourceFile,
                        destFile);
                }

                if (AfterFileEndpointsPinnedForTestAsync != null)
                {
                    await AfterFileEndpointsPinnedForTestAsync(
                        action,
                        sourceFile,
                        destFile);
                }

                if (!lease.SourceParent.VisiblePathMatches()
                    || !lease.DestinationParent.VisiblePathMatches())
                {
                    return false;
                }

                using var sourceEntry = lease.SourceParent.OpenExistingFile(
                    lease.SourceName,
                    requireDeleteAccess: false);
                using var destinationEntry =
                    lease.DestinationParent.TryOpenExistingFile(
                        lease.DestinationName,
                        requireDeleteAccess: true);
                if (destinationEntry != null
                    && sourceEntry.IdentifiesSameEntry(destinationEntry))
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        action,
                        sourceFile,
                        destFile,
                        "Source and destination identify the same file generation");
                    return false;
                }

                if (capturePublication != null
                    && action == FileAction.HardlinkCopy
                    && registrationOperationId.HasValue
                    && destinationEntry != null)
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        action,
                        sourceFile,
                        destFile,
                        "A retryable hardlink registration destination is already owned by another publication state");
                    return false;
                }

                if (AfterFileEntriesPinnedForTestAsync != null)
                {
                    await AfterFileEntriesPinnedForTestAsync(
                        action,
                        sourceFile,
                        destFile);
                }

                if (!lease.SourceParent.VisiblePathMatches()
                    || !lease.DestinationParent.VisiblePathMatches()
                    || !sourceEntry.VisiblePathMatches()
                    || (destinationEntry != null
                        && !destinationEntry.VisiblePathMatches()))
                {
                    return false;
                }

                var forceByteCopyAfterDurableFallback = false;
                if (preferHardlink
                    && capturePublication != null
                    && registrationOperationId.HasValue
                    && destinationEntry == null)
                {
                    var publication =
                        await TryPublishHardlinkRegistrationAsync(
                            lease,
                            sourceEntry,
                            destFile,
                            registrationOperationId.Value);
                    if (publication.Outcome
                        == HardlinkRegistrationPublicationOutcome.Published)
                    {
                        CapturePublishedRegistrationLease(
                            publication.Lease
                                ?? throw new InvalidOperationException(
                                    "Durable hardlink publication completed without a registration lease."),
                            capturePublication);
                        LogMutation(
                            FileMutationOutcome.Success,
                            action,
                            sourceFile,
                            destFile,
                            "Published through an operation-scoped durable hardlink claim");
                        return true;
                    }

                    if (publication.Outcome
                        != HardlinkRegistrationPublicationOutcome.FallbackAllowed)
                    {
                        _logger.LogWarning(
                            publication.Failure,
                            "Durable hardlink publication requires reconciliation before another publication strategy can run: {Source} -> {Destination}",
                            LogRedaction.SanitizeFilePath(sourceFile),
                            LogRedaction.SanitizeFilePath(destFile));
                        return false;
                    }

                    forceByteCopyAfterDurableFallback = true;
                    _logger.LogInformation(
                        publication.Failure,
                        "Durable hardlink publication was unavailable before ownership evidence was retained; falling back to a pinned byte copy: {Source} -> {Destination}",
                        LogRedaction.SanitizeFilePath(sourceFile),
                        LogRedaction.SanitizeFilePath(destFile));
                }

                var sourceContent = await CaptureFileMoveContentAsync(sourceEntry);
                if (AfterPinnedSourceContentCapturedForTestAsync != null)
                {
                    await AfterPinnedSourceContentCapturedForTestAsync(
                        action,
                        sourceFile,
                        destFile);
                }
                if (destinationEntry != null
                    && await FileMatchesMoveContentAsync(
                        destinationEntry,
                        sourceContent)
                    && sourceEntry.VisiblePathMatches()
                    && await FileMatchesMoveContentAsync(
                        sourceEntry,
                        sourceContent))
                {
                    if (capturePublication != null)
                    {
                        CapturePublishedRegistrationLease(
                            PinnedAudiobookFileRegistrationLease.Create(
                                destinationEntry.OpenStableRegistrationCopy(),
                                destFile,
                                sourcePhysicalObjectIdentity:
                                    sourceEntry.GetObjectIdentity()),
                            capturePublication);
                    }
                    LogMutation(
                        FileMutationOutcome.Skipped,
                        action,
                        sourceFile,
                        destFile,
                        "Destination already contains the pinned source bytes");
                    return true;
                }

                var temporaryName =
                    $".listenarr-file-copy-{Guid.NewGuid():N}.tmp";
                PinnedDirectoryCreation.PinnedFileEntry? prepared = null;
                var published = false;
                var durablePublicationOwnsPrepared = false;
                try
                {
                    if (preferHardlink
                        && !forceByteCopyAfterDurableFallback)
                    {
                        try
                        {
                            if (BeforePinnedHardlinkCreationForTestAsync != null)
                            {
                                await BeforePinnedHardlinkCreationForTestAsync();
                            }
                            prepared = sourceEntry.CreateHardLinkTo(
                                lease.DestinationParent,
                                temporaryName);
                        }
                        catch (Exception exception) when (exception is
                            IOException or System.ComponentModel.Win32Exception
                                or PlatformNotSupportedException)
                        {
                            _logger.LogInformation(
                                exception,
                                "Pinned hardlink creation was unavailable; falling back to a pinned byte copy: {Source} -> {Destination}",
                                LogRedaction.SanitizeFilePath(sourceFile),
                                LogRedaction.SanitizeFilePath(destFile));
                        }
                    }

                    if (prepared == null)
                    {
                        prepared = lease.DestinationParent.CreateNewFile(
                            temporaryName);
                        await using (var sourceStream = sourceEntry.OpenReadStream(
                            bufferSize: 128 * 1024,
                            asynchronous: false))
                        await using (var destinationStream = prepared.OpenWriteStream(
                            bufferSize: 128 * 1024,
                            asynchronous: false))
                        {
                            sourceStream.Position = 0;
                            await sourceStream.CopyToAsync(destinationStream);
                            await destinationStream.FlushAsync();
                            destinationStream.Flush(flushToDisk: true);
                        }
                        sourceEntry.PreserveMetadataTo(prepared);
                    }

                    if (!lease.SourceParent.VisiblePathMatches()
                        || !lease.DestinationParent.VisiblePathMatches()
                        || !sourceEntry.VisiblePathMatches()
                        || !prepared.VisiblePathMatches()
                        || !await FileMatchesMoveContentAsync(
                            sourceEntry,
                            sourceContent)
                        || !await FileMatchesMoveContentAsync(
                            prepared,
                            sourceContent)
                        || (destinationEntry != null
                            && !destinationEntry.VisiblePathMatches()))
                    {
                        return false;
                    }

                    if (destinationEntry == null)
                    {
                        using var appearedDestination =
                            lease.DestinationParent.TryOpenExistingFile(
                                lease.DestinationName,
                                requireDeleteAccess: false);
                        if (appearedDestination != null)
                        {
                            return false;
                        }

                        prepared.MoveWithinParent(lease.DestinationName);
                    }
                    else
                    {
                        await PublishPreparedFileReplacingCapturedDestinationAsync(
                            prepared,
                            sourceEntry.GetObjectIdentity(),
                            lease.DestinationParent,
                            lease.DestinationName,
                            destinationEntry,
                            publicationStateName,
                            () => durablePublicationOwnsPrepared = true);
                    }
                    published = true;
                    if (capturePublication != null)
                    {
                        CapturePublishedRegistrationLease(
                            PinnedAudiobookFileRegistrationLease.Create(
                                prepared.OpenStableRegistrationCopy(),
                                destFile,
                                sourcePhysicalObjectIdentity:
                                    sourceEntry.GetObjectIdentity()),
                            capturePublication);
                    }
                    LogMutation(
                        FileMutationOutcome.Success,
                        action,
                        sourceFile,
                        destFile,
                        preferHardlink && sourceEntry.IdentifiesSameEntry(prepared)
                            ? "Created from the pinned source hardlink generation"
                            : "Copied from stable pinned source bytes");
                    return true;
                }
                finally
                {
                    if (!published
                        && !durablePublicationOwnsPrepared
                        && prepared != null
                        && prepared.VisiblePathMatches())
                    {
                        prepared.Delete(immediateWindows: true);
                    }
                    prepared?.Dispose();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(
                    ex,
                    "Pinned file copy failed: {Source} -> {Dest}",
                    sourceFile,
                    destFile);
                return false;
            }
        }

    }
}
