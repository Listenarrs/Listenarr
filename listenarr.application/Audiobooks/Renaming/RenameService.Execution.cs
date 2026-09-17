using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private async Task<FileRenameResultItem> ExecuteFileRenameAsync(
        Audiobook audiobook,
        FileRenameOperation fileOperation,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        RenameExecutionPlan executionPlan,
        CancellationToken cancellationToken)
    {
        var source = NormalizePath(fileOperation.CurrentPath);
        var destination = NormalizePath(fileOperation.NewPath);
        var item = new FileRenameResultItem
        {
            FileId = fileOperation.FileId,
            PreviousPath = source,
            NewPath = destination
        };
        if (string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(destination))
        {
            item.Error = "File organize operation is missing a source or destination path.";
            return item;
        }

        var trackedSourcePath = ResolveTrackedSourcePath(
            audiobook,
            fileOperation,
            semantics,
            out var databaseFile,
            out var trackedPathError);
        if (trackedPathError != null)
        {
            item.Error = trackedPathError;
            return item;
        }

        if (!PathsEqual(source, trackedSourcePath, semantics))
        {
            item.Error = "Source path does not match the tracked audiobook file.";
            return item;
        }

        if (!IsPathWithinAllowedRoots(source, allowedRoots, semantics)
            || !IsPathWithinAllowedRoots(destination, allowedRoots, semantics))
        {
            item.Error = "File path is outside the allowed library roots.";
            return item;
        }

        if (!_fileSystem.TryValidateMutationTarget(
                source,
                allowedRoots,
                out var validatedSource,
                out _)
            || !_fileSystem.TryValidateMutationTarget(
                destination,
                allowedRoots,
                out var validatedDestination,
                out _))
        {
            item.Error = "File path could not be resolved safely within the allowed library roots.";
            return item;
        }

        source = validatedSource;
        destination = validatedDestination;
        item.PreviousPath = source;
        item.NewPath = destination;

        if (!_fileSystem.FileExists(source))
        {
            item.Error = "Source file not found.";
            return item;
        }

        if (_fileSystem.FileExists(destination)
            && !PathsEqual(source, destination, semantics))
        {
            item.Error = "Target file already exists.";
            return item;
        }

        try
        {
            var destinationIdentity = await _filePathIdentityResolver.ResolveAsync(
                audiobook,
                destination,
                cancellationToken);
            if (destinationIdentity.State != PathIdentityState.Valid)
            {
                item.Error = "Destination filesystem identity is unavailable.";
                return item;
            }

            var targetDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && !executionPlan.UseVerifiedProtocol)
            {
                await EnsureOwnedRenameHierarchyAsync(
                    targetDirectory,
                    allowedRoots,
                    semantics,
                    audiobook.Id,
                    Guid.NewGuid(),
                    cancellationToken);
            }

            if (!PathsEqual(source, destination, semantics))
            {
                // Rename journals are owner-bound and discovered directly during startup
                // recovery. Use a fresh durable ID for each organize attempt so a fully
                // compensated terminal journal can never block a later legitimate retry of
                // the same source/destination paths.
                var operationId = Guid.NewGuid();
                item.OperationId = operationId;
                bool moved;
                if (executionPlan.UseVerifiedProtocol)
                {
                    var sourceProof = FindSourceProof(
                        executionPlan,
                        fileOperation.FileId);
                    if (!sourceProof.HasValue
                        || !executionPlan.BatchManifest.HasValue)
                    {
                        item.Error =
                            "Verified organize source proof is unavailable.";
                        return item;
                    }

                    var preparation = await _verifiedFileRenameTransactionCoordinator
                        .PrepareAsync(
                            source,
                            destination,
                            operationId,
                            executionPlan.BatchId,
                            executionPlan.BatchManifest.Value,
                            audiobook.Id,
                            databaseFile?.Id ?? 0,
                            sourceProof.Value,
                            cancellationToken);
                    moved = preparation.Success && preparation.Lease != null;
                    item.VerifiedRenameLease = preparation.Lease;
                    if (!moved)
                    {
                        item.Error = preparation.Error
                            ?? "Verified file organize operation failed.";
                        return item;
                    }
                }
                else if (databaseFile != null)
                {
                    if (string.IsNullOrWhiteSpace(
                            databaseFile.PhysicalObjectIdentity))
                    {
                        item.Error =
                            "Tracked source physical identity is unavailable.";
                        return item;
                    }

                    moved = await _fileMover
                        .MoveFilePreservingPhysicalIdentityAsync(
                            source,
                            destination,
                            databaseFile.PhysicalObjectIdentity,
                            operationId,
                            audiobook.Id,
                            databaseFile.Id);
                }
                else
                {
                    moved = await _fileMover.PerformActionOn(
                        FileAction.Move,
                        source,
                        destination,
                        operationId,
                        audiobook.Id,
                        audiobookFileId: 0);
                }

                if (!moved)
                {
                    item.Error = "File move operation failed.";
                    return item;
                }
            }

            if (databaseFile != null)
            {
                databaseFile.ApplyPathIdentity(destination, destinationIdentity);
                if (executionPlan.UseVerifiedProtocol
                    && !PathsEqual(source, destination, semantics))
                {
                    databaseFile.ClearPhysicalObjectIdentity();
                }
            }
            else if (fileOperation.FileId == 0
                && !string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                audiobook.FilePath = destination;
            }

            item.Success = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            _logger.LogError(
                exception,
                "Failed to organize file {FileId} for audiobook {AudiobookId}",
                fileOperation.FileId,
                audiobook.Id);
            item.Error = "File organize operation failed.";
        }

        return item;
    }
}
