namespace Listenarr.Application.Search.Matching;

/// <summary>Partial names are derived only from credited library narrators.</summary>
internal sealed class NarratorNameCatalog
{
    public static NarratorNameCatalog Empty { get; } = new([], ReleaseAnnotationCatalog.Unavailable);
    private readonly Dictionary<string, string[]> _names;

    public NarratorNameCatalog(IReadOnlyList<string> narrators, ReleaseAnnotationCatalog catalog)
    {
        _names = narrators.Count > 64 ? [] : narrators.SelectMany(n => Variants(n, catalog).Select(v => (Key: Key(v), Name: n)))
            .GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Resolve(string value) => _names.GetValueOrDefault(Key(value)) ?? [];

    public static IEnumerable<string> Variants(string name, ReleaseAnnotationCatalog catalog)
    {
        if (name.Length > 256 || catalog.CollectiveCredits.Contains(IdentityText.Canonical(name))) return [];
        var words = IdentityText.Canonical(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.')).Where(w => w.Length > 0).ToArray();
        if (words.Length < 2) return [];
        var count = words.Length;
        string? suffix = null;
        if (catalog.NameSuffixes.Contains(words[^1])) { suffix = words[^1]; count--; }
        if (count < 2) return [];
        var start = count - 1;
        while (start > 1 && catalog.NameParticles.Contains(words[start - 1])) start--;
        var surname = string.Join(' ', words[start..count]);
        return suffix == null ? [surname] : [surname, surname + " " + suffix];
    }

    private static string Key(string value) => IdentityText.Canonical(ReleaseTextView.SceneWords(value))
        .TrimEnd('.', ',').TrimEnd().ToUpperInvariant();
}
