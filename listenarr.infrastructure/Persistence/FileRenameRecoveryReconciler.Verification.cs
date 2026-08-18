using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRenameRecoveryReconciler
{
    private static bool IsTransientRecoveryFilesystemException(Exception exception)
    {
        if (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        if (exception is System.ComponentModel.Win32Exception native)
        {
            return native.NativeErrorCode is 5 or 13 or 16 or 30 or 32 or 33;
        }

        return exception is InvalidOperationException { InnerException: not null }
            && IsTransientRecoveryFilesystemException(exception.InnerException);
    }

    private static GenerationMatchOutcome ProbeTargetGeneration(FileMutationJournal journal) =>
        ProbePathGeneration(
            journal.DestinationPath,
            journal.TargetPhysicalObjectIdentity);

    private static GenerationMatchOutcome ProbeSourceGeneration(FileMutationJournal journal) =>
        ProbePathGeneration(
            journal.SourcePath,
            journal.SourcePhysicalObjectIdentity);

    private static GenerationMatchOutcome ProbePathGeneration(
        string path,
        string? expectedPhysicalObjectIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity))
        {
            return GenerationMatchOutcome.Mismatch;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return GenerationMatchOutcome.Mismatch;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var file = parent.OpenExistingFileForStableRead(Path.GetFileName(fullPath));
            var fileVisibility = file.ProbeVisiblePathMatch();
            var parentVisibility = parent.ProbeVisiblePathMatch();
            if (fileVisibility == RegistrationPublicationMatchOutcome.Unavailable
                || parentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                return GenerationMatchOutcome.Unavailable;
            }
            if (fileVisibility != RegistrationPublicationMatchOutcome.Match
                || parentVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                return GenerationMatchOutcome.Mismatch;
            }

            return file.MatchesObjectIdentity(expectedPhysicalObjectIdentity)
                ? GenerationMatchOutcome.Match
                : GenerationMatchOutcome.Mismatch;
        }
        catch (System.ComponentModel.Win32Exception exception) when (
            OperatingSystem.IsWindows()
                ? exception.NativeErrorCode is 2 or 3
                : exception.NativeErrorCode == 2)
        {
            return GenerationMatchOutcome.Mismatch;
        }
        catch (FileNotFoundException)
        {
            return GenerationMatchOutcome.Mismatch;
        }
        catch (DirectoryNotFoundException)
        {
            return GenerationMatchOutcome.Mismatch;
        }
        catch (Exception exception) when (IsTransientRecoveryFilesystemException(exception))
        {
            return GenerationMatchOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return GenerationMatchOutcome.Mismatch;
        }
    }

    private async Task<bool?> OwnerMetadataPointsToSourceAsync(
        Audiobook audiobook,
        AudiobookFile? audiobookFile,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (audiobookFile != null)
        {
            if (string.IsNullOrWhiteSpace(audiobookFile.Path))
            {
                return false;
            }

            var identity = await identityResolver.ResolveAsync(
                audiobook,
                audiobookFile.Path,
                cancellationToken);
            if (identity.State == PathIdentityState.Unavailable)
            {
                return null;
            }
            return identity.State == PathIdentityState.Valid
                && FileSystemPathIdentity.AreEquivalent(
                    identity.CanonicalPath,
                    journal.SourcePath,
                    new FileSystemPathSemantics(
                        identity.Syntax,
                        identity.CaseSensitivity));
        }

        if (string.IsNullOrWhiteSpace(audiobook.FilePath))
        {
            return false;
        }
        var resolution = await semanticsResolver.ResolveAsync(
            journal.SourcePath,
            FileSystemCaseSensitivityMode.Auto,
            cancellationToken);
        if (resolution.State == PathIdentityState.Unavailable)
        {
            return null;
        }
        if (resolution.State != PathIdentityState.Valid)
        {
            return false;
        }

        var storedSource = ResolveAbsoluteStoredPath(
            audiobook.FilePath,
            audiobook.BasePath);
        return FileSystemPathIdentity.AreEquivalent(
            storedSource,
            journal.SourcePath,
            resolution.Semantics);
    }

    private async Task<PathNormalizationOutcome> NormalizeAudiobookPathsAsync(
        Audiobook audiobook,
        CancellationToken cancellationToken)
    {
        var oldBasePath = audiobook.BasePath;
        var resolvedFiles = new List<(
            AudiobookFile File,
            string Path,
            AudiobookFilePathIdentity Identity)>();
        foreach (var file in audiobook.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            var absolutePath = ResolveAbsoluteStoredPath(file.Path, oldBasePath);
            var identity = await identityResolver.ResolveAsync(
                audiobook,
                absolutePath,
                cancellationToken);
            if (identity.State == PathIdentityState.Unavailable)
            {
                return PathNormalizationOutcome.Unavailable;
            }
            if (identity.State != PathIdentityState.Valid)
            {
                return PathNormalizationOutcome.Conflict;
            }
            resolvedFiles.Add((file, absolutePath, identity));
        }

        foreach (var resolved in resolvedFiles)
        {
            resolved.File.ApplyPathIdentity(resolved.Path, resolved.Identity);
        }

        if (resolvedFiles.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var absoluteLegacyPath = ResolveAbsoluteStoredPath(
                    audiobook.FilePath,
                    oldBasePath);
                audiobook.FilePath = absoluteLegacyPath;
                audiobook.BasePath = Path.GetDirectoryName(absoluteLegacyPath);
            }
            return PathNormalizationOutcome.Success;
        }

        var firstIdentity = resolvedFiles[0].Identity;
        var semantics = new FileSystemPathSemantics(
            firstIdentity.Syntax,
            firstIdentity.CaseSensitivity);
        if (resolvedFiles.Any(item =>
                item.Identity.Syntax != semantics.Syntax
                || item.Identity.CaseSensitivity != semantics.CaseSensitivity))
        {
            return PathNormalizationOutcome.Conflict;
        }

        var directories = resolvedFiles
            .Select(item => Path.GetDirectoryName(item.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        var commonBasePath = FileUtils.GetCommonPathForDirectories(directories, semantics);
        if (string.IsNullOrWhiteSpace(commonBasePath))
        {
            return PathNormalizationOutcome.Conflict;
        }
        audiobook.BasePath = commonBasePath;
        var primary = resolvedFiles
            .OrderBy(item => item.Path, semantics.Comparer)
            .First();
        audiobook.FilePath = primary.Path;
        if (primary.File.Size > 0)
        {
            audiobook.FileSize = primary.File.Size;
        }

        return PathNormalizationOutcome.Success;
    }

    private static string ResolveAbsoluteStoredPath(string path, string? basePath)
    {
        if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var absolutePath,
                out _))
        {
            return absolutePath;
        }
        if (string.IsNullOrWhiteSpace(basePath)
            || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                basePath,
                out var canonicalBase,
                out _)
            || Path.IsPathRooted(path))
        {
            throw new InvalidOperationException(
                "A recovered audiobook file path cannot be resolved against its stored base path.");
        }

        return Path.GetFullPath(Path.Combine(canonicalBase, path));
    }

    private enum GenerationMatchOutcome
    {
        Match,
        Mismatch,
        Unavailable
    }

    private enum PathNormalizationOutcome
    {
        Success,
        Conflict,
        Unavailable
    }
}
