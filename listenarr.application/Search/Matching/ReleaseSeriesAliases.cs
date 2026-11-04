namespace Listenarr.Application.Search.Matching;

/// <summary>Semantic aliases are scoped to a stored series, never generated from a release.</summary>
internal static class ReleaseSeriesAliases
{
    private static readonly (string Canonical, string Alias)[] Entries =
    [
        ("The First Law Trilogy", "The First Law"),
        ("Presidential Agent", "Presidential Agents"),
        ("The Boxcar Children Mysteries", "Boxcar Children"),
        ("Apprentice Adept Series", "Apprentice Adept")
    ];

    public static IEnumerable<string> For(string name)
    {
        var key = ReleaseSeriesCatalog.Key(name);
        foreach (var entry in Entries)
            if (ReleaseSeriesCatalog.Key(entry.Canonical) == key) yield return entry.Alias;
    }
}
