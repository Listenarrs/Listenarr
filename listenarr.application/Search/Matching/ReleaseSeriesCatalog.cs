namespace Listenarr.Application.Search.Matching;

/// <summary>Immutable series-name index, built once when a context snapshots its references.</summary>
internal sealed class ReleaseSeriesCatalog
{
    internal sealed record Fact(string Name, decimal[] Positions, bool Invalid);
    internal sealed record Binding(Fact Fact, bool Alias);
    public static ReleaseSeriesCatalog Empty { get; } = new([]);
    private readonly Dictionary<string, Binding[]> _names;
    public IReadOnlyList<Fact> Facts { get; }
    public int MaxNameLength { get; }

    public ReleaseSeriesCatalog(IReadOnlyList<ReleaseSeriesReference> references)
    {
        // The interpreter reports the reference budget violation before using this index.
        if (references.Count > 64) { Facts = []; _names = []; return; }
        Facts = references.GroupBy(r => Key(r.Name), StringComparer.Ordinal)
            .Select(group => new Fact(group.First().Name,
                group.Where(r => ReleaseSeriesRule.TryPosition(r.Position, out _))
                    .Select(r => { ReleaseSeriesRule.TryPosition(r.Position, out var p); return p; }).Distinct().Order().ToArray(),
                group.Any(r => !string.IsNullOrWhiteSpace(r.Position) && !ReleaseSeriesRule.TryPosition(r.Position, out _))))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        var names = new List<(string Key, Binding Binding)>();
        foreach (var fact in Facts)
        {
            Add(fact.Name, false);
            foreach (var alias in ReleaseSeriesAliases.For(fact.Name)) Add(alias, true);
            void Add(string name, bool alias)
            {
                var key = Key(name);
                names.Add((key, new(fact, alias)));
                if (key.StartsWith("THE ", StringComparison.Ordinal)) names.Add((key[4..], new(fact, true)));
            }
        }
        _names = names.GroupBy(n => n.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(n => n.Binding).Distinct().ToArray(), StringComparer.Ordinal);
        MaxNameLength = _names.Keys.Select(k => k.Length).DefaultIfEmpty().Max();
    }

    public IReadOnlyList<Binding> Resolve(string value) => _names.GetValueOrDefault(Key(value)) ?? [];

    public static string Key(string value)
    {
        var text = ReleaseTextView.SceneWords(value).ToCharArray();
        for (var i = 0; i < text.Length; i++)
            if (text[i] is ':' or ',' || text[i] == '.' && !(i > 0 && i + 1 < text.Length
                && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))) text[i] = ' ';
        return IdentityText.Canonical(new string(text)).ToUpperInvariant();
    }
}
