using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    private async Task<IReadOnlyList<RootFolder>?> GetRootFoldersForSemanticsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await rootFolderService.GetAllAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogDebug(ex, "Failed to load root folders while resolving audiobook file path semantics");
            return null;
        }
    }

    private async Task<LibraryPathSemanticsResolution?> ResolveLibraryPathSemanticsAsync(
        string path,
        IReadOnlyList<RootFolder>? rootFolders,
        CancellationToken cancellationToken)
    {
        if (rootFolders == null)
        {
            return null;
        }

        LibraryPathSemanticsResolution? bestResolution = null;
        var bestRootLength = -1;
        foreach (var root in rootFolders)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                continue;
            }

            try
            {
                var rootResolution = await semanticsResolver.ResolveAsync(
                    canonicalRoot,
                    root.CaseSensitivityMode,
                    cancellationToken);
                if (rootResolution.State != PathIdentityState.Valid
                    || !FileSystemPathIdentity.IsSameOrInside(
                        path,
                        canonicalRoot,
                        rootResolution.Semantics))
                {
                    continue;
                }

                if (canonicalRoot.Length > bestRootLength)
                {
                    bestResolution = new LibraryPathSemanticsResolution(
                        rootResolution.Semantics,
                        canonicalRoot);
                    bestRootLength = canonicalRoot.Length;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve configured root folder semantics for {RootPath}",
                    LogRedaction.SanitizeFilePath(root.Path));
            }
        }

        if (bestResolution != null)
        {
            return bestResolution;
        }

        try
        {
            var resolution = await semanticsResolver.ResolveAsync(
                path,
                cancellationToken: cancellationToken);
            return resolution.State == PathIdentityState.Valid
                ? new LibraryPathSemanticsResolution(resolution.Semantics, null)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogDebug(
                ex,
                "Failed to resolve audiobook file path semantics for {Path}",
                LogRedaction.SanitizeFilePath(path));
            return null;
        }
    }

    private string? ResolvePhysicalSafetyRoot(
        string candidatePath,
        string authorizationRoot,
        LibraryPathSemanticsResolution resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution.ConfiguredRootPath))
        {
            return authorizationRoot;
        }

        try
        {
            var configuredRoot = resolution.ConfiguredRootPath;
            if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    configuredRoot,
                    authorizationRoot,
                    resolution.Semantics,
                    out var authorizationRelativePath)
                || !FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    configuredRoot,
                    candidatePath,
                    resolution.Semantics,
                    out var candidateRelativePath))
            {
                return authorizationRoot;
            }

            var separators = resolution.Semantics.Syntax == FileSystemPathSyntax.Windows
                ? new[] { '\\', '/' }
                : new[] { '/' };
            var authorizationSegments = authorizationRelativePath.Split(
                separators,
                StringSplitOptions.RemoveEmptyEntries);
            var candidateSegments = candidateRelativePath.Split(
                separators,
                StringSplitOptions.RemoveEmptyEntries);
            if (candidateSegments.Length < authorizationSegments.Length)
            {
                return authorizationRoot;
            }

            if (authorizationSegments.Length == 0)
            {
                return configuredRoot;
            }

            var separator = resolution.Semantics.Syntax == FileSystemPathSyntax.Windows
                ? '\\'
                : '/';
            var physicalRelativePath = string.Join(
                separator,
                candidateSegments.Take(authorizationSegments.Length));
            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    configuredRoot,
                    physicalRelativePath,
                    resolution.Semantics,
                    out var physicalRoot))
            {
                return authorizationRoot;
            }

            var currentRelativePath = string.Empty;
            foreach (var segment in candidateSegments.Take(authorizationSegments.Length))
            {
                currentRelativePath = string.IsNullOrEmpty(currentRelativePath)
                    ? segment
                    : currentRelativePath + separator + segment;
                if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                        configuredRoot,
                        currentRelativePath,
                        resolution.Semantics,
                        out var currentPhysicalPath)
                    || fileSystem.IsReparsePoint(currentPhysicalPath))
                {
                    return null;
                }
            }

            return physicalRoot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            return authorizationRoot;
        }
    }

    private sealed record LibraryPathSemanticsResolution(
        FileSystemPathSemantics Semantics,
        string? ConfiguredRootPath);
}
