using System.ComponentModel;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CopyFileWithPinnedRetryAsync(
        AudiobookContentMoveRequest request,
        string sourceRoot,
        string target,
        string sourceFile,
        string destinationFile,
        MoveJobEntry manifestEntry,
        string destinationRoot,
        bool destinationIsJobOwnedTemp,
        bool destinationHasStructuredOwnership,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var (sourceSegments, sourceName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            sourceSemantics);
        var (destinationSegments, destinationName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            targetSemantics);
        var partialName = destinationName + $".listenarr-{request.JobId:N}.partial";
        var partialFile = Path.Join(
            Path.GetDirectoryName(destinationFile)
                ?? throw new MoveNeedsAttentionException(
                    "The copy destination file has no parent directory."),
            partialName);

        try
        {
            using var sourcePath = PinnedMoveDirectoryPath.OpenExisting(
                sourceRoot,
                sourceSegments);
            using var destinationPath = await PinnedMoveDirectoryPath.OpenOrCreateAsync(
                destinationRoot,
                destinationSegments,
                () => EnsureMutationAuthorizedAsync(
                    request,
                    sourceRoot,
                    target,
                    cancellationToken));
            ValidatePinnedParentPath(
                sourcePath.Current.FullPath,
                sourceFile,
                sourceSemantics,
                "copy source");
            ValidatePinnedParentPath(
                destinationPath.Current.FullPath,
                destinationFile,
                targetSemantics,
                "copy destination");

            for (var attempt = 1; attempt <= MaxCopyAttempts; attempt++)
            {
                PinnedDirectoryCreation.PinnedFileEntry? createdPartial = null;
                try
                {
                    sourcePath.EnsureVisibleHierarchy();
                    destinationPath.EnsureVisibleHierarchy();
                    using var sourceEntry = sourcePath.Current.OpenExistingFile(
                        sourceName,
                        requireDeleteAccess: false);
                    ValidatePinnedSourcePhysicalIdentity(
                        request,
                        manifestEntry,
                        sourceEntry);
                    if (!sourceEntry.VisiblePathMatches()
                        || !await sourceEntry.MatchesAsync(
                            manifestEntry.Length,
                            manifestEntry.Sha256,
                            cancellationToken))
                    {
                        throw new MoveNeedsAttentionException(
                            $"Source file no longer matches the persisted move manifest: {manifestEntry.RelativePath}");
                    }

                    if (File.Exists(destinationFile))
                    {
                        using var destinationEntry = destinationPath.Current.OpenExistingFile(
                            destinationName,
                            requireDeleteAccess: destinationIsJobOwnedTemp);
                        if (await destinationEntry.MatchesAsync(
                                manifestEntry.Length,
                                manifestEntry.Sha256,
                                cancellationToken))
                        {
                            await RemovePinnedPartialIfPresentAsync(
                                request,
                                sourceRoot,
                                target,
                                destinationPath,
                                partialName,
                                partialFile,
                                manifestEntry,
                                destinationIsJobOwnedTemp,
                                destinationHasStructuredOwnership,
                                cancellationToken);
                            logger.LogInformation(
                                "Skipping copy for move job {JobId}; destination already matches the persisted manifest: {Destination}",
                                request.JobId,
                                LogRedaction.SanitizeFilePath(destinationFile));
                            return;
                        }

                        if (!destinationIsJobOwnedTemp)
                        {
                            throw new MoveNeedsAttentionException(
                                $"Destination file differs from the move manifest and will not be overwritten: {destinationName}");
                        }

                        await EnsureMutationAuthorizedAsync(
                            request,
                            sourceRoot,
                            target,
                            cancellationToken);
                        destinationPath.EnsureVisibleHierarchy();
                        if (!destinationEntry.VisiblePathMatches())
                        {
                            throw new MoveNeedsAttentionException(
                                "The owned destination file changed before replacement cleanup.");
                        }
                        destinationEntry.Delete();
                    }

                    if (File.Exists(partialFile))
                    {
                        if (!destinationHasStructuredOwnership)
                        {
                            throw new MoveNeedsAttentionException(
                                "A job-shaped partial file exists without structured move ownership.");
                        }

                        using var partialEntry = destinationPath.Current.OpenExistingFile(
                            partialName,
                            requireDeleteAccess: true);
                        if (await partialEntry.MatchesAsync(
                                manifestEntry.Length,
                                manifestEntry.Sha256,
                                cancellationToken))
                        {
                            await PublishPinnedPartialAsync(
                                request,
                                sourceRoot,
                                target,
                                sourcePath,
                                sourceEntry,
                                destinationPath,
                                partialEntry,
                                destinationName,
                                destinationFile,
                                manifestEntry,
                                cancellationToken);
                            return;
                        }

                        if (!destinationIsJobOwnedTemp)
                        {
                            throw new MoveNeedsAttentionException(
                                $"A direct-copy partial file does not match the persisted manifest and was preserved: {partialName}");
                        }

                        await EnsureMutationAuthorizedAsync(
                            request,
                            sourceRoot,
                            target,
                            cancellationToken);
                        destinationPath.EnsureVisibleHierarchy();
                        if (!partialEntry.VisiblePathMatches())
                        {
                            throw new MoveNeedsAttentionException(
                                "The owned partial file changed before cleanup.");
                        }
                        partialEntry.Delete();
                    }

                    await EnsureMutationAuthorizedAsync(
                        request,
                        sourceRoot,
                        target,
                        cancellationToken);
                    sourcePath.EnsureVisibleHierarchy();
                    destinationPath.EnsureVisibleHierarchy();
                    if (!sourceEntry.VisiblePathMatches()
                        || !await sourceEntry.MatchesAsync(
                            manifestEntry.Length,
                            manifestEntry.Sha256,
                            cancellationToken))
                    {
                        throw new MoveNeedsAttentionException(
                            $"Source file changed before pinned copying: {manifestEntry.RelativePath}");
                    }

                    faultInjector?.OnCopyMutation(
                        request.JobId,
                        CopyMutationFaultPoint.BeforePartialFileCreation);
                    await EnsureMutationAuthorizedAsync(
                        request,
                        sourceRoot,
                        target,
                        cancellationToken);
                    sourcePath.EnsureVisibleHierarchy();
                    destinationPath.EnsureVisibleHierarchy();
                    if (!sourceEntry.VisiblePathMatches()
                        || File.Exists(partialFile)
                        || Directory.Exists(partialFile))
                    {
                        throw new MoveNeedsAttentionException(
                            $"The copy source or partial destination changed at creation: {manifestEntry.RelativePath}");
                    }

                    createdPartial = destinationPath.Current.CreateNewFile(partialName);
                    await CopyFileWithLeaseChecksAsync(
                        request,
                        sourceRoot,
                        target,
                        sourceEntry,
                        createdPartial,
                        cancellationToken);
                    await EnsureMutationAuthorizedAsync(
                        request,
                        sourceRoot,
                        target,
                        cancellationToken);
                    TryPreservePinnedFileMetadata(sourceEntry, createdPartial, sourceFile);
                    if (!await createdPartial.MatchesAsync(
                            manifestEntry.Length,
                            manifestEntry.Sha256,
                            cancellationToken))
                    {
                        createdPartial.Delete();
                        throw new IOException(
                            "Temporary move copy failed persisted-manifest verification.");
                    }

                    if (File.Exists(destinationFile))
                    {
                        using var destinationEntry = destinationPath.Current.OpenExistingFile(
                            destinationName,
                            requireDeleteAccess: false);
                        if (await destinationEntry.MatchesAsync(
                                manifestEntry.Length,
                                manifestEntry.Sha256,
                                cancellationToken))
                        {
                            await EnsureMutationAuthorizedAsync(
                                request,
                                sourceRoot,
                                target,
                                cancellationToken);
                            createdPartial.Delete();
                            return;
                        }

                        throw new MoveNeedsAttentionException(
                            $"Destination file appeared during the move and differs from the manifest: {destinationName}");
                    }

                    await PublishPinnedPartialAsync(
                        request,
                        sourceRoot,
                        target,
                        sourcePath,
                        sourceEntry,
                        destinationPath,
                        createdPartial,
                        destinationName,
                        destinationFile,
                        manifestEntry,
                        cancellationToken);
                    return;
                }
                catch (MoveNeedsAttentionException)
                {
                    throw;
                }
                catch (IOException exception) when (attempt < MaxCopyAttempts)
                {
                    if (createdPartial != null)
                    {
                        await EnsureMutationAuthorizedAsync(
                            request,
                            sourceRoot,
                            target,
                            cancellationToken);
                        destinationPath.EnsureVisibleHierarchy();
                        if (createdPartial.VisiblePathMatches())
                        {
                            createdPartial.Delete();
                        }
                    }

                    logger.LogWarning(
                        exception,
                        "IO error copying file {File} attempt {Attempt}",
                        LogRedaction.SanitizeFilePath(sourceFile),
                        attempt);
                    var delay = TimeSpan.FromSeconds(
                        Math.Min(8, Math.Pow(2, attempt - 1)));
                    await Task.Delay(delay, cancellationToken);
                }
                finally
                {
                    createdPartial?.Dispose();
                }
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            throw new MoveNeedsAttentionException(
                $"The move file could not be copied through pinned filesystem handles: {exception.Message}");
        }

        throw new IOException(
            $"Failed to copy file after {MaxCopyAttempts} attempts: {sourceFile}");
    }

    private async Task PublishPinnedPartialAsync(
        AudiobookContentMoveRequest request,
        string sourceRoot,
        string target,
        PinnedMoveDirectoryPath sourcePath,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        PinnedMoveDirectoryPath destinationPath,
        PinnedDirectoryCreation.PinnedFileEntry partialEntry,
        string destinationName,
        string destinationFile,
        MoveJobEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        faultInjector?.OnCopyMutation(
            request.JobId,
            CopyMutationFaultPoint.BeforePartialPublication);
        await EnsureMutationAuthorizedAsync(
            request,
            sourceRoot,
            target,
            cancellationToken);
        sourcePath.EnsureVisibleHierarchy();
        destinationPath.EnsureVisibleHierarchy();
        if (!sourceEntry.VisiblePathMatches()
            || !partialEntry.VisiblePathMatches()
            || !await sourceEntry.MatchesAsync(
                manifestEntry.Length,
                manifestEntry.Sha256,
                cancellationToken)
            || !await partialEntry.MatchesAsync(
                manifestEntry.Length,
                manifestEntry.Sha256,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"The source or partial copy changed before publication: {manifestEntry.RelativePath}");
        }
        if (File.Exists(destinationFile) || Directory.Exists(destinationFile))
        {
            throw new MoveNeedsAttentionException(
                $"The copy destination appeared before publication: {destinationName}");
        }

        partialEntry.MoveWithinParent(destinationName);
    }

    private async Task RemovePinnedPartialIfPresentAsync(
        AudiobookContentMoveRequest request,
        string sourceRoot,
        string target,
        PinnedMoveDirectoryPath destinationPath,
        string partialName,
        string partialFile,
        MoveJobEntry manifestEntry,
        bool destinationIsJobOwnedTemp,
        bool destinationHasStructuredOwnership,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(partialFile))
        {
            return;
        }
        if (!destinationHasStructuredOwnership)
        {
            throw new MoveNeedsAttentionException(
                "A job-shaped partial file exists without structured move ownership.");
        }

        using var partialEntry = destinationPath.Current.OpenExistingFile(
            partialName,
            requireDeleteAccess: true);
        if (!destinationIsJobOwnedTemp
            && !await partialEntry.MatchesAsync(
                manifestEntry.Length,
                manifestEntry.Sha256,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"A direct-copy partial file does not match the persisted manifest and was preserved: {partialName}");
        }

        await EnsureMutationAuthorizedAsync(
            request,
            sourceRoot,
            target,
            cancellationToken);
        destinationPath.EnsureVisibleHierarchy();
        if (!partialEntry.VisiblePathMatches()
            || (!destinationIsJobOwnedTemp
                && !await partialEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken)))
        {
            throw new MoveNeedsAttentionException(
                $"The partial file changed before cleanup and was preserved: {partialName}");
        }

        partialEntry.Delete();
    }

    private void TryPreservePinnedFileMetadata(
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        PinnedDirectoryCreation.PinnedFileEntry destinationEntry,
        string sourceFile)
    {
        try
        {
            sourceEntry.PreserveMetadataTo(destinationEntry);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogDebug(
                exception,
                "Non-fatal: failed to preserve attributes for {File}",
                LogRedaction.SanitizeFilePath(sourceFile));
        }
    }
}
