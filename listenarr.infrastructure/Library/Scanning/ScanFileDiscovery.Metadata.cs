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
            .Select(Path.GetFileName)
            .Any(name =>
            {
                // Match the folder's original normalization first so a dotted folder name still
                // matches a dotted record (both directions), then fall back to the collapsed and
                // stripped aliases so a folder carrying post-nominals matches clean author
                // metadata (symmetric to BuildExpectedAuthorTokens).
                var original = NormalizeMetadataToken(name);
                if (authorTokens.Contains(original))
                {
                    return true;
                }

                var collapsed = NormalizeMetadataToken(
                    CollapseTrailingDottedPostNominals(name));
                return authorTokens.Contains(collapsed)
                    || authorTokens.Contains(StripTrailingPostNominals(collapsed));
            });
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

    // Professional and generational post-nominals, in normalized (lowercase, punctuation-free)
    // form. Deliberately curated to unambiguous multi-letter credentials and generational
    // markers; short degree abbreviations that collide with real name words (e.g. "ma" as in
    // Yo-Yo Ma, "do", "ba", "bs") are intentionally excluded to avoid stripping surnames.
    private static readonly HashSet<string> AuthorPostNominals = new(StringComparer.Ordinal)
    {
        "phd", "md", "dds", "dvm", "dpt", "edd", "psyd", "jd", "esq",
        "mba", "cpa", "llm", "llb", "mfa",
        "jr", "sr", "ii", "iii", "iv",
    };

    private static HashSet<string> BuildExpectedAuthorTokens(Audiobook audiobook)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var author in audiobook.Authors ?? [])
        {
            // Keep the original author token so an exact dotted record/tag author (e.g.
            // "Jane Doe, Ph.D.") still matches the embedded-artist comparison, which uses the
            // original normalization.
            AddToken(tokens, author);

            // Also collapse dotted credentials ("Jane Doe, Ph.D." -> "Jane Doe phd") so both the
            // full token and the stripped token below see them as a single word.
            var collapsedAuthor = CollapseTrailingDottedPostNominals(author);
            AddToken(tokens, collapsedAuthor);

            // Also accept a directory that omits professional/generational post-nominals,
            // e.g. metadata "Jane Doe, PhD" matching a "Jane Doe" folder. This is additive
            // (never removes a token) and covers post-nominals only — initials are NOT
            // expanded, which would over-match distinct authors.
            var normalized = NormalizeMetadataToken(collapsedAuthor);
            var stripped = StripTrailingPostNominals(normalized);
            if (!string.IsNullOrWhiteSpace(stripped)
                && !string.Equals(stripped, normalized, StringComparison.Ordinal))
            {
                tokens.Add(stripped);
            }
        }

        return tokens;
    }

    // Rewrites trailing DOTTED spellings of the known post-nominals to their joined form so the
    // subsequent token normalization keeps them as one token instead of splitting on the inner
    // dots. Without this, "Jane Doe, Ph.D." normalizes to "jane doe ph d" and the trailing-word
    // stripper never sees "phd". Only trailing, dotted, curated-list credentials are collapsed —
    // spaced initials (e.g. "L. M. Montgomery", or a trailing "Montgomery L.M.") are left intact
    // so a real name is never rewritten, and at least one leading word is always preserved.
    // Shared by the metadata and folder sides so the two stay symmetric.
    private static string CollapseTrailingDottedPostNominals(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        var words = value.Split(
            [' ', '\t', '\n', '\r', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            return value;
        }

        var collapsed = false;
        var end = words.Length;
        // Walk across trailing credentials even when a plain one (e.g. "CPA") sits after a dotted
        // one (e.g. "Ph.D."), so a mixed stack like "Ph.D., CPA" still reaches the dotted word.
        // Only dotted spellings are rewritten; stripping just the periods keeps strings such as
        // "Ph.D.2" ("phd2") from being accepted as a credential, and at least one word is kept.
        while (end > 1)
        {
            var word = words[end - 1];
            var letters = word.Replace(".", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            if (!AuthorPostNominals.Contains(letters))
            {
                break;
            }

            if (word.Contains('.'))
            {
                words[end - 1] = letters;
                collapsed = true;
            }

            end--;
        }

        return collapsed ? string.Join(' ', words) : value;
    }

    // Removes trailing post-nominal words from an already-normalized token, stopping at the
    // first non-post-nominal word and always leaving at least one word, so a name never
    // collapses to nothing.
    private static string StripTrailingPostNominals(string normalizedToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return string.Empty;
        }

        var words = normalizedToken.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var end = words.Length;
        while (end > 1 && AuthorPostNominals.Contains(words[end - 1]))
        {
            end--;
        }

        return end == words.Length
            ? normalizedToken
            : string.Join(' ', words.Take(end));
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
