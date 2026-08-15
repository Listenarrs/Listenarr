using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

public static class TrackedFilePathIndexBuilder
{
    public static HashSet<string> Build(
        IEnumerable<AudiobookFile> files,
        IEnumerable<Audiobook> audiobooks,
        FileSystemPathSemantics comparisonSemantics) =>
        ResolvePaths(files, audiobooks, comparisonSemantics)
            .ToHashSet(comparisonSemantics.Comparer);

    public static IReadOnlyList<string> ResolvePaths(
        IEnumerable<AudiobookFile> files,
        IEnumerable<Audiobook> audiobooks,
        FileSystemPathSemantics comparisonSemantics)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(audiobooks);
        if (comparisonSemantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Tracked file filtering requires resolved filesystem semantics.");
        }

        var audiobookList = audiobooks.ToList();
        var basePaths = audiobookList.ToDictionary(
            audiobook => audiobook.Id,
            audiobook => audiobook.BasePath);
        var tracked = new List<string>();

        foreach (var file in files)
        {
            if (TryResolveFilePath(
                    file,
                    basePaths.GetValueOrDefault(file.AudiobookId),
                    comparisonSemantics,
                    out var resolved))
            {
                tracked.Add(resolved);
            }
        }

        foreach (var audiobook in audiobookList)
        {
            if (TryResolveStoredPath(
                    audiobook.FilePath,
                    audiobook.BasePath,
                    comparisonSemantics,
                    out var resolved))
            {
                tracked.Add(resolved);
            }
        }

        return tracked;
    }

    private static bool TryResolveFilePath(
        AudiobookFile file,
        string? basePath,
        FileSystemPathSemantics comparisonSemantics,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (file.PathIdentityState == PathIdentityState.Valid
            && file.PathSyntax == comparisonSemantics.Syntax
            && !string.IsNullOrWhiteSpace(file.CanonicalPath))
        {
            try
            {
                resolvedPath = FileSystemPathIdentity.Canonicalize(
                    file.CanonicalPath,
                    comparisonSemantics.Syntax);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return TryResolveStoredPath(
            file.Path,
            basePath,
            comparisonSemantics,
            out resolvedPath);
    }

    private static bool TryResolveStoredPath(
        string? storedPath,
        string? basePath,
        FileSystemPathSemantics comparisonSemantics,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return false;
        }

        try
        {
            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    storedPath,
                    comparisonSemantics.Syntax,
                    out _))
            {
                resolvedPath = FileSystemPathIdentity.Canonicalize(
                    storedPath,
                    comparisonSemantics.Syntax);
                return true;
            }

            if (string.IsNullOrWhiteSpace(basePath)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    basePath,
                    storedPath,
                    comparisonSemantics,
                    out var absolutePath))
            {
                return false;
            }

            resolvedPath = FileSystemPathIdentity.Canonicalize(
                absolutePath,
                comparisonSemantics.Syntax);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }
}
