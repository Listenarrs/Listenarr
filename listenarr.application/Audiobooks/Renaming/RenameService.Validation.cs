using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private async Task<RenameResult?> ValidateOperationPlanAsync(
        Audiobook audiobook,
        RenameOperation operation,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var result = new RenameResult { AudiobookId = operation.AudiobookId };
        var ownershipKeys = new HashSet<string>(StringComparer.Ordinal);
        var fileIds = new HashSet<int>();
        var fileOperations = operation.FileRenames ?? [];

        foreach (var trackedFile in audiobook.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(trackedFile.Path))
            {
                result.Error = "A tracked audiobook file path is missing or invalid.";
                result.Conflict = true;
                return result;
            }

            _ = ResolveStoredFilePath(
                audiobook,
                trackedFile.Path,
                semantics,
                "Tracked audiobook file path is missing or invalid.",
                out var trackedPathError);
            if (trackedPathError != null)
            {
                result.Error = trackedPathError;
                result.Conflict = true;
                return result;
            }
        }

        if (!string.IsNullOrWhiteSpace(operation.NewFolderPath))
        {
            var expectedFileIds = GetTrackedFileIdsForFolderChange(audiobook);
            var requestedFileIds = fileOperations
                .Select(file => file.FileId)
                .ToHashSet();
            if (expectedFileIds.Count == 0
                || !expectedFileIds.SetEquals(requestedFileIds))
            {
                result.Error = "A folder-changing organize request must include every tracked audiobook file.";
                result.Conflict = true;
                return result;
            }
        }

        foreach (var fileOperation in fileOperations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new FileRenameResultItem
            {
                FileId = fileOperation.FileId,
                PreviousPath = NormalizePath(fileOperation.CurrentPath),
                NewPath = NormalizePath(fileOperation.NewPath)
            };

            if (!fileIds.Add(fileOperation.FileId))
            {
                item.Error = "The organize request contains the same file more than once.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                return result;
            }

            var trackedSourcePath = ResolveTrackedSourcePath(
                audiobook,
                fileOperation,
                semantics,
                out var dbFile,
                out var error);
            if (error != null)
            {
                item.Error = error;
                result.RenamedFiles.Add(item);
                result.Error = error;
                return result;
            }

            if (!PathsEqual(item.PreviousPath, trackedSourcePath, semantics))
            {
                item.Error = "Source path does not match the tracked audiobook file.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                result.Conflict = true;
                return result;
            }

            if (!IsPathWithinAllowedRoots(item.PreviousPath!, allowedRoots, semantics)
                || !IsPathWithinAllowedRoots(item.NewPath!, allowedRoots, semantics))
            {
                item.Error = "File path is outside the allowed library roots.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                return result;
            }

            if (!_fileSystem.TryValidateMutationTarget(
                    item.PreviousPath!,
                    allowedRoots,
                    out var validatedSource,
                    out _)
                || !_fileSystem.TryValidateMutationTarget(
                    item.NewPath!,
                    allowedRoots,
                    out var validatedDestination,
                    out _))
            {
                item.Error = "File path could not be resolved safely within the allowed library roots.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                return result;
            }

            item.PreviousPath = validatedSource;
            item.NewPath = validatedDestination;
            if (!string.IsNullOrWhiteSpace(operation.NewFolderPath)
                && !FileSystemPathIdentity.IsSameOrInside(
                    validatedDestination,
                    operation.NewFolderPath,
                    semantics))
            {
                item.Error = "A file destination is outside the requested audiobook folder.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                return result;
            }

            if (!_fileSystem.FileExists(validatedSource))
            {
                item.Error = "Source file not found.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                result.Conflict = true;
                return result;
            }

            if (_fileSystem.FileExists(validatedDestination)
                && !PathsEqual(validatedSource, validatedDestination, semantics))
            {
                item.Error = "Target file already exists.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                return result;
            }

            var destinationIdentity = await _filePathIdentityResolver.ResolveAsync(
                audiobook,
                validatedDestination,
                cancellationToken);
            if (destinationIdentity.State != PathIdentityState.Valid
                || string.IsNullOrWhiteSpace(destinationIdentity.OwnershipKey))
            {
                item.Error = "Destination filesystem identity is unavailable.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                return result;
            }

            if (!ownershipKeys.Add(destinationIdentity.OwnershipKey))
            {
                item.Error = "The organize request contains duplicate destination paths.";
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                return result;
            }

            var ownership = await _audiobookFileRepository.CheckOwnershipAsync(
                audiobook.Id,
                dbFile?.Id,
                destinationIdentity,
                cancellationToken);
            if (ownership.Outcome != AudiobookFileOwnershipCheckOutcome.Available)
            {
                item.Error = ownership.Outcome switch
                {
                    AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook =>
                        "Target file is owned by another audiobook.",
                    AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook =>
                        "Target file is already owned by another file record for this audiobook.",
                    AudiobookFileOwnershipCheckOutcome.IdentityConflict =>
                        "Target file identity conflicts with existing ownership data.",
                    _ => "Target file identity is unavailable."
                };
                result.RenamedFiles.Add(item);
                result.Error = item.Error;
                result.Conflict = ownership.Outcome is
                    AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook or
                    AudiobookFileOwnershipCheckOutcome.IdentityConflict;
                return result;
            }
        }

        return null;
    }

    private static string ResolveTrackedSourcePath(
        Audiobook audiobook,
        FileRenameOperation fileOperation,
        FileSystemPathSemantics semantics,
        out AudiobookFile? dbFile,
        out string? error)
    {
        dbFile = null;
        error = null;
        if (fileOperation.FileId == 0)
        {
            return ResolveStoredFilePath(
                audiobook,
                audiobook.FilePath,
                semantics,
                "Legacy file organize operation does not match a tracked audiobook file.",
                out error);
        }

        dbFile = audiobook.Files?.FirstOrDefault(file => file.Id == fileOperation.FileId);
        if (dbFile == null)
        {
            error = "File does not belong to this audiobook.";
            return string.Empty;
        }

        return ResolveStoredFilePath(
            audiobook,
            dbFile.Path,
            semantics,
            "Tracked audiobook file path is missing or invalid.",
            out error);
    }

    private static string ResolveStoredFilePath(
        Audiobook audiobook,
        string? storedPath,
        FileSystemPathSemantics semantics,
        string missingOrInvalidError,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            error = missingOrInvalidError;
            return string.Empty;
        }

        try
        {
            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    storedPath,
                    semantics.Syntax,
                    out _))
            {
                return FileSystemPathIdentity.Canonicalize(storedPath, semantics.Syntax);
            }
            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _))
            {
                error = "Tracked audiobook file path uses an unexpected filesystem syntax.";
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath,
                    storedPath,
                    semantics,
                    out var resolvedPath))
            {
                error = missingOrInvalidError;
                return string.Empty;
            }

            return resolvedPath;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            error = missingOrInvalidError;
            return string.Empty;
        }
    }

    private static RenameResult StalePreviewResult(
        int audiobookId,
        string error) =>
        new()
        {
            AudiobookId = audiobookId,
            Success = false,
            Conflict = true,
            Error = error
        };
}
