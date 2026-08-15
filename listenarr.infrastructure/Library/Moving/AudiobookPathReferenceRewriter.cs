using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class AudiobookPathRewriteException(string message)
    : InvalidOperationException(message);

internal static class AudiobookPathReferenceRewriter
{
    public static void Rewrite(
        Audiobook audiobook,
        string? sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemCaseSensitivityMode targetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
        IReadOnlyDictionary<string, string>? targetPhysicalObjectIdentities = null,
        DateTime? targetPhysicalIdentityObservedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBasePath);

        EnsureCurrentBasePathMatchesExpectedState(
            audiobook.BasePath,
            sourceBasePath,
            targetBasePath,
            sourceSemantics,
            targetSemantics);

        if (targetPhysicalObjectIdentities != null
            && targetPhysicalIdentityObservedAtUtc is not { Kind: DateTimeKind.Utc })
        {
            throw new ArgumentException(
                "Moved physical identity observation time must be UTC.",
                nameof(targetPhysicalIdentityObservedAtUtc));
        }

        var filePath = audiobook.FilePath;
        var imageUrl = audiobook.ImageUrl;
        var rewrittenFiles = new List<(
            AudiobookFile File,
            string Path,
            AudiobookFilePathIdentity Identity,
            string? PhysicalObjectIdentity)>();

        if (!string.IsNullOrWhiteSpace(sourceBasePath))
        {
            filePath = RewriteAbsoluteReference(
                audiobook.FilePath,
                sourceBasePath,
                targetBasePath,
                sourceSemantics,
                targetSemantics);
            imageUrl = RewriteAbsoluteReference(
                audiobook.ImageUrl,
                sourceBasePath,
                targetBasePath,
                sourceSemantics,
                targetSemantics);

            foreach (var file in audiobook.Files ?? [])
            {
                var rewrittenPath = RewriteAbsoluteReference(
                    file.Path,
                    sourceBasePath,
                    targetBasePath,
                    sourceSemantics,
                    targetSemantics);
                if (string.IsNullOrWhiteSpace(rewrittenPath))
                {
                    throw new AudiobookPathRewriteException(
                        "A tracked audiobook file path is missing and cannot be rewritten.");
                }

                var storedFilePath = file.Path ?? string.Empty;
                var isRelative = !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                        storedFilePath,
                        sourceSemantics.Syntax,
                        out _)
                    && !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                        storedFilePath,
                        out _);
                var isAlreadyUnderTarget = IsSameOrInside(
                    rewrittenPath,
                    targetBasePath,
                    targetSemantics);
                if (isRelative
                    || !StoredPathsMatch(file.Path!, rewrittenPath)
                    || isAlreadyUnderTarget)
                {
                    var targetIdentity = CreateTargetIdentity(
                        rewrittenPath,
                        targetBasePath,
                        targetSemantics,
                        targetCaseSensitivityMode);
                    string? targetPhysicalObjectIdentity = null;
                    if (targetPhysicalObjectIdentities != null
                        && !targetPhysicalObjectIdentities.TryGetValue(
                            targetIdentity.CanonicalPath,
                            out targetPhysicalObjectIdentity))
                    {
                        throw new AudiobookPathRewriteException(
                            "A moved tracked audiobook file has no verified target physical generation.");
                    }

                    rewrittenFiles.Add((
                        file,
                        rewrittenPath,
                        targetIdentity,
                        targetPhysicalObjectIdentity));
                }
            }
        }

        // Apply rewritten references only after every path has been validated so
        // one bad stored value cannot leave the audiobook half-rebased.
        audiobook.FilePath = filePath;
        audiobook.ImageUrl = imageUrl;
        foreach (var (file, path, identity, physicalObjectIdentity) in rewrittenFiles)
        {
            file.ApplyPathIdentity(path, identity);
            if (physicalObjectIdentity != null)
            {
                file.ApplyPhysicalObjectIdentity(
                    physicalObjectIdentity,
                    targetPhysicalIdentityObservedAtUtc!.Value);
            }
            else
            {
                // A metadata-only rewrite has no proof that the target pathname
                // identifies the same physical generation as the old source.
                file.ClearPhysicalObjectIdentity();
            }
        }

        audiobook.BasePath = targetBasePath;
    }

    private static void EnsureCurrentBasePathMatchesExpectedState(
        string? currentBasePath,
        string? sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        if (string.IsNullOrWhiteSpace(currentBasePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sourceBasePath))
        {
            if (StoredPathsMatch(currentBasePath, targetBasePath)
                || PathsMatch(currentBasePath, targetBasePath, targetSemantics))
            {
                return;
            }

            throw new AudiobookPathRewriteException(
                "The audiobook path changed before its path references could be rewritten.");
        }

        if (StoredPathsMatch(currentBasePath, sourceBasePath)
            || PathsMatch(currentBasePath, sourceBasePath, sourceSemantics)
            || IsSameOrInside(sourceBasePath, currentBasePath, sourceSemantics)
            || StoredPathsMatch(currentBasePath, targetBasePath)
            || PathsMatch(currentBasePath, targetBasePath, targetSemantics))
        {
            return;
        }

        throw new AudiobookPathRewriteException(
            "The audiobook path changed before its path references could be rewritten.");
    }

    private static bool StoredPathsMatch(string currentPath, string expectedPath) =>
        string.Equals(currentPath, expectedPath, StringComparison.Ordinal);

    private static bool IsSameOrInside(
        string path,
        string basePath,
        FileSystemPathSemantics semantics)
    {
        try
        {
            return FileSystemPathIdentity.IsSameOrInside(path, basePath, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool PathsMatch(
        string path,
        string expectedPath,
        FileSystemPathSemantics semantics)
    {
        try
        {
            return FileSystemPathIdentity.AreEquivalent(path, expectedPath, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? RewriteAbsoluteReference(
        string? path,
        string sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        if (string.IsNullOrWhiteSpace(path)
            || IsRemoteUri(path))
        {
            return path;
        }

        bool isInsideSource;
        try
        {
            isInsideSource = FileSystemPathIdentity.IsSameOrInside(path, sourceBasePath, sourceSemantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // Relative and non-filesystem references are intentionally preserved.
            return path;
        }

        if (!isInsideSource)
        {
            return path;
        }

        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                sourceBasePath,
                path,
                sourceSemantics,
                out var relativePath))
        {
            throw new AudiobookPathRewriteException(
                $"Stored audiobook path '{path}' could not be mapped to the new base path.");
        }

        if (string.IsNullOrEmpty(relativePath))
        {
            return targetBasePath;
        }

        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                targetBasePath,
                FileSystemPathIdentity.ConvertRelativePathSyntax(
                    relativePath,
                    sourceSemantics.Syntax,
                    targetSemantics.Syntax),
                targetSemantics,
                out var rewrittenPath))
        {
            throw new AudiobookPathRewriteException(
                $"Stored audiobook path '{path}' could not be mapped to the new base path.");
        }

        return rewrittenPath;
    }

    private static AudiobookFilePathIdentity CreateTargetIdentity(
        string storedPath,
        string targetBasePath,
        FileSystemPathSemantics targetSemantics,
        FileSystemCaseSensitivityMode targetCaseSensitivityMode)
    {
        string absolutePath;
        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                storedPath,
                targetSemantics.Syntax,
                out _))
        {
            absolutePath = FileSystemPathIdentity.Canonicalize(
                storedPath,
                targetSemantics.Syntax);
        }
        else if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _))
        {
            throw new AudiobookPathRewriteException(
                "A rewritten audiobook file path uses a different filesystem syntax than the target root.");
        }
        else if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                     targetBasePath,
                     storedPath,
                     targetSemantics,
                     out absolutePath))
        {
            throw new AudiobookPathRewriteException(
                "A relative audiobook file path could not be resolved within the target base path.");
        }

        return AudiobookFilePathIdentity.CreateValid(
            absolutePath,
            targetSemantics,
            targetCaseSensitivityMode,
            targetBasePath);
    }

    private static bool IsRemoteUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile;
}
