using System.Globalization;
using System.Text;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning;

internal static partial class ScanFileDiscovery
{
    internal static string NormalizeMetadataToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var pendingSeparator = false;
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    internal static bool MetadataMatchesAudiobook(
        AudioMetadata metadata,
        Audiobook audiobook)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(audiobook);

        var expectedIdentifier = NormalizeMetadataToken(audiobook.Asin);
        var metadataIdentifier = NormalizeMetadataToken(metadata.Asin);
        if (!string.IsNullOrEmpty(expectedIdentifier)
            && string.Equals(
                expectedIdentifier,
                metadataIdentifier,
                StringComparison.Ordinal))
        {
            return true;
        }

        var expectedTitle = NormalizeMetadataToken(audiobook.Title);
        if (string.IsNullOrEmpty(expectedTitle))
        {
            return false;
        }

        var metadataTitles = new[]
        {
            NormalizeMetadataToken(metadata.Album),
            NormalizeMetadataToken(metadata.Title)
        };
        if (!metadataTitles.Contains(expectedTitle, StringComparer.Ordinal))
        {
            return false;
        }

        var expectedAuthors = BuildExpectedAuthorTokens(audiobook);
        if (expectedAuthors.Count == 0)
        {
            return true;
        }

        var metadataAuthors = new[]
        {
            NormalizeMetadataToken(metadata.AlbumArtist),
            NormalizeMetadataToken(metadata.Artist)
        };
        return metadataAuthors.Any(author =>
            !string.IsNullOrEmpty(author)
            && expectedAuthors.Contains(author));
    }

    private static string? TryFindTitleBoundary(
        string candidate,
        string canonicalRoot,
        IReadOnlySet<string> titleTokens,
        IReadOnlySet<string> authorTokens,
        FileSystemPathSemantics semantics,
        bool requireAuthorContext)
    {
        if (titleTokens.Count == 0)
        {
            return null;
        }

        foreach (var ancestor in EnumerateAncestorsWithinRoot(
            Path.GetDirectoryName(candidate),
            canonicalRoot,
            semantics))
        {
            var segment = Path.GetFileName(ancestor);
            if (!SegmentMatchesExpectedTitle(segment, titleTokens))
            {
                continue;
            }

            if (!requireAuthorContext
                || FileSystemPathIdentity.AreEquivalent(
                    ancestor,
                    canonicalRoot,
                    semantics)
                || HasAuthorContext(
                    Path.GetDirectoryName(ancestor),
                    canonicalRoot,
                    authorTokens,
                    semantics))
            {
                return ancestor;
            }
        }

        return null;
    }

    private static bool SegmentMatchesExpectedTitle(
        string segment,
        IReadOnlySet<string> titleTokens)
    {
        var normalizedSegment = NormalizeMetadataToken(segment);
        if (titleTokens.Contains(normalizedSegment))
        {
            return true;
        }

        var components = segment
            .Split(
                [" - ", " – ", " — "],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeMetadataToken)
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .ToArray();
        var normalizedSuffix = string.Empty;
        for (var index = components.Length - 1; index >= 0; index--)
        {
            normalizedSuffix = string.IsNullOrEmpty(normalizedSuffix)
                ? components[index]
                : $"{components[index]} {normalizedSuffix}";
            if (titleTokens.Contains(normalizedSuffix))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryFindIdentifierBoundary(
        string candidate,
        string canonicalRoot,
        IReadOnlySet<string> identifiers,
        FileSystemPathSemantics semantics)
    {
        if (identifiers.Count == 0)
        {
            return null;
        }

        foreach (var ancestor in EnumerateAncestorsWithinRoot(
            Path.GetDirectoryName(candidate),
            canonicalRoot,
            semantics))
        {
            var tokens = NormalizeMetadataToken(Path.GetFileName(ancestor))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(identifiers.Contains))
            {
                return ancestor;
            }
        }

        var fileTokens = NormalizeMetadataToken(Path.GetFileNameWithoutExtension(candidate))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fileTokens.Any(identifiers.Contains)
            ? Path.GetDirectoryName(candidate)
            : null;
    }

    private static bool FileNameMatchesExpectedTitle(
        string candidate,
        IReadOnlySet<string> titleTokens) =>
        titleTokens.Contains(NormalizeMetadataToken(
            Path.GetFileNameWithoutExtension(candidate)));

    private static bool HasAuthorContext(
        string? startingDirectory,
        string canonicalRoot,
        IReadOnlySet<string> authorTokens,
        FileSystemPathSemantics semantics)
    {
        if (authorTokens.Count == 0)
        {
            return true;
        }

        return EnumerateAncestorsWithinRoot(
                startingDirectory,
                canonicalRoot,
                semantics)
            .Select(path => NormalizeMetadataToken(Path.GetFileName(path)))
            .Any(authorTokens.Contains);
    }

    private static IEnumerable<string> EnumerateAncestorsWithinRoot(
        string? startingDirectory,
        string canonicalRoot,
        FileSystemPathSemantics semantics)
    {
        var current = startingDirectory;
        while (!string.IsNullOrWhiteSpace(current)
            && FileSystemPathIdentity.IsSameOrInside(
                current,
                canonicalRoot,
                semantics))
        {
            yield return FileSystemPathIdentity.Canonicalize(
                current,
                semantics.Syntax);
            if (FileSystemPathIdentity.AreEquivalent(
                    current,
                    canonicalRoot,
                    semantics))
            {
                yield break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || FileSystemPathIdentity.AreEquivalent(parent, current, semantics))
            {
                yield break;
            }

            current = parent;
        }
    }

    private static HashSet<string> BuildExpectedTitleTokens(Audiobook audiobook)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        AddToken(tokens, audiobook.Title);
        AddToken(tokens, JoinMetadata(audiobook.Title, audiobook.Subtitle));
        AddToken(tokens, JoinMetadata(audiobook.Title, audiobook.Edition));
        AddToken(tokens, JoinMetadata(audiobook.Title, audiobook.PublishYear));
        AddToken(tokens, JoinMetadata(audiobook.Title, audiobook.Asin));
        AddToken(tokens, JoinMetadata(
            audiobook.Title,
            audiobook.PublishYear,
            audiobook.Asin));
        AddToken(tokens, JoinMetadata(audiobook.Title, audiobook.SeriesNumber));
        return tokens;
    }

    private static HashSet<string> BuildExpectedAuthorTokens(Audiobook audiobook)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var author in audiobook.Authors ?? [])
        {
            AddToken(tokens, author);
        }

        return tokens;
    }

    private static HashSet<string> BuildExpectedIdentifierTokens(Audiobook audiobook)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        AddToken(tokens, audiobook.Asin);
        AddToken(tokens, audiobook.OpenLibraryId);
        return tokens;
    }

    private static string JoinMetadata(params string?[] values) =>
        string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static void AddToken(ISet<string> tokens, string? value)
    {
        var normalized = NormalizeMetadataToken(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            tokens.Add(normalized);
        }
    }
}
