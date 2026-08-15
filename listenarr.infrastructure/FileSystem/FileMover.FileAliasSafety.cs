using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> IsFilesystemAliasAsync(
        string source,
        string destination)
    {
        var sourcePath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(source));
        var destinationPath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(destination));
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryResolvePhysicalPath(sourcePath, out var sourceResolution)
            && TryResolvePhysicalPath(destinationPath, out var destinationResolution)
            && sourceResolution.EntryKind == PhysicalPathEntryKind.File
            && destinationResolution.EntryKind == PhysicalPathEntryKind.File
            && (sourceResolution.EncounteredLink || destinationResolution.EncounteredLink))
        {
            if (string.Equals(
                    sourceResolution.ResolvedPath,
                    destinationResolution.ResolvedPath,
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (_semanticsResolver != null)
            {
                try
                {
                    var resolution = await _semanticsResolver.ResolveAsync(sourcePath);
                    if (resolution.State == PathIdentityState.Valid
                        && FileSystemPathIdentity.AreEquivalent(
                            sourceResolution.ResolvedPath,
                            destinationResolution.ResolvedPath,
                            resolution.Semantics))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException or ArgumentException
                        or InvalidOperationException or NotSupportedException or PathTooLongException)
                {
                    // Fall through to object identity instead of declaring uncertain linked
                    // paths distinct.
                }
            }
        }

        var pathEquivalence = await TryDetermineFilesystemPathEquivalenceAsync(
            sourcePath,
            destinationPath);
        if (pathEquivalence == true)
        {
            // Case-equivalent spellings of one unlinked pathname remain an idempotent no-op.
            return false;
        }

        return TryGetRegularFileIdentity(sourcePath, out var sourceIdentity)
            && TryGetRegularFileIdentity(destinationPath, out var destinationIdentity)
            && sourceIdentity == destinationIdentity;
    }
}
