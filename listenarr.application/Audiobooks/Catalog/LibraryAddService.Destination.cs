using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Catalog;

public partial class LibraryAddService
{
    private async Task<LibraryAddOperationResult?> ResolveAndValidateDestinationAsync(
        Audiobook audiobook,
        AudibleBookMetadata metadata,
        LibraryAddOperationRequest request,
        CancellationToken cancellationToken)
    {
        var configuredRootFolders = await _rootFolderService.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationSettings? settings = null;
        if (configuredRootFolders.Count == 0
            || string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            settings = await _configurationService.GetApplicationSettingsAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        var allowedDestinationRoots = FileUtils.GetValidMutationRootsForCurrentOs(
            configuredRootFolders.Count > 0
                ? configuredRootFolders.Select(root => root.Path)
                : [settings!.OutputPath]);

        var requestedBaseDirectory = request.DestinationPath;
        if (!string.IsNullOrWhiteSpace(requestedBaseDirectory))
        {
            if (FileUtils.HasLeadingWhitespaceBeforeRootedPath(requestedBaseDirectory))
            {
                return ValidationFailure(
                    "destination_path_invalid",
                    "DestinationPath is invalid: leading whitespace before an absolute path is not allowed.",
                    requestedBaseDirectory);
            }

            if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                requestedBaseDirectory,
                out var normalizedRequestedBaseDirectory,
                out var validationReason,
                rejectParentTraversal: true))
            {
                return ValidationFailure(
                    "destination_path_invalid",
                    $"DestinationPath is invalid: {validationReason}",
                    requestedBaseDirectory);
            }

            if (allowedDestinationRoots.Count == 0
                || !_fileSystem.TryValidateMutationTarget(
                    normalizedRequestedBaseDirectory,
                    allowedDestinationRoots,
                    out normalizedRequestedBaseDirectory,
                    out _))
            {
                return ValidationFailure(
                    "destination_path_outside_roots",
                    "DestinationPath must be inside a configured root folder or output path",
                    normalizedRequestedBaseDirectory);
            }

            audiobook.BasePath = normalizedRequestedBaseDirectory;
        }
        else
        {
            var rootFolder = await _rootFolderService.GetDefaultAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var baseDirectory = rootFolder != null ? rootFolder.Path : settings!.OutputPath;
            var generatedBasePath = Path.Join(
                baseDirectory,
                _fileNamingService.ApplyNamingPattern(settings!.FolderNamingPattern, metadata));
            if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                generatedBasePath,
                out var normalizedGeneratedBasePath,
                out var validationReason,
                rejectParentTraversal: true))
            {
                return ValidationFailure(
                    "destination_path_invalid",
                    $"Generated library destination is invalid: {validationReason}",
                    generatedBasePath);
            }

            if (allowedDestinationRoots.Count == 0
                || !_fileSystem.TryValidateMutationTarget(
                    normalizedGeneratedBasePath,
                    allowedDestinationRoots,
                    out normalizedGeneratedBasePath,
                    out _))
            {
                return ValidationFailure(
                    "destination_path_outside_roots",
                    "Generated library destination must be inside a configured root folder or output path",
                    normalizedGeneratedBasePath);
            }

            audiobook.BasePath = normalizedGeneratedBasePath;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var existingDestinationOwner = await FindExistingDestinationOwnerAsync(
            audiobook.BasePath!,
            configuredRootFolders,
            cancellationToken);
        if (existingDestinationOwner != null)
        {
            if (RepresentsSameDestinationAudiobook(
                    existingDestinationOwner,
                    audiobook))
            {
                // Repeating the same add against its already-committed destination is
                // idempotent. No filesystem mutation will occur, so relocation/mutation
                // blockers do not need to authorize that existing library location.
                return AlreadyExists(existingDestinationOwner);
            }

            return ValidationFailure(
                "destination_path_blocked",
                "Destination is already assigned to another audiobook in the library.",
                audiobook.BasePath);
        }

        var destinationBlockingReason = await _destinationMutationGuard.GetBlockingReasonAsync(
            audiobook.BasePath!,
            cancellationToken);
        return destinationBlockingReason == null
            ? null
            : ValidationFailure(
                "destination_path_blocked",
                destinationBlockingReason,
                audiobook.BasePath);
    }
}
