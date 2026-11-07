namespace Listenarr.Application.Search.Matching;

/// <summary>Immutable series-name index, built once when a context snapshots its references.</summary>
internal sealed class ReleaseSeriesCatalog
{
    internal sealed record Fact(string Name, decimal[] Positions, bool Invalid);
    internal sealed record Binding(Fact Fact, bool Alias);
    public static ReleaseSeriesCatalog Empty { get; } = new([], ReleaseAnnotationCatalog.Unavailable);
    private readonly Dictionary<string, Binding[]> _names;
    private readonly Dictionary<string, Binding[]> _exact;
    private readonly string[] _descriptorSuffixes = [];
    private readonly SeriesWordPair[] _wordPairs = [];
    public IReadOnlyList<Fact> Facts { get; }
    public int MaxNameLength { get; }

    public ReleaseSeriesCatalog(IReadOnlyList<ReleaseSeriesReference> references, ReleaseAnnotationCatalog catalog)
    {
        // The interpreter reports the reference budget violation before using this index.
        if (references.Count > 64) { Facts = []; _names = []; _exact = []; return; }
        Facts = references.GroupBy(r => Key(r.Name), StringComparer.Ordinal)
            .Select(group => new Fact(group.First().Name,
                group.Where(r => ReleaseSeriesRule.TryPosition(r.Position, out _))
                    .Select(r => { ReleaseSeriesRule.TryPosition(r.Position, out var p); return p; }).Distinct().Order().ToArray(),
                group.Any(r => !string.IsNullOrWhiteSpace(r.Position) && !ReleaseSeriesRule.TryPosition(r.Position, out _))))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        if (Facts.Count == 0) { _names = []; _exact = []; return; }
        _descriptorSuffixes = catalog.SeriesDescriptors.Select(s => " " + s.ToUpperInvariant()).ToArray();
        _wordPairs = catalog.SeriesWordPairs.Select(p => new SeriesWordPair(p.Singular.ToUpperInvariant(), p.Plural.ToUpperInvariant())).ToArray();
        var names = new List<(string Key, Binding Binding)>();
        _exact = Facts.ToDictionary(f => Key(f.Name), f => new[] { new Binding(f, false) }, StringComparer.Ordinal);
        foreach (var fact in Facts)
        {
            Add(fact.Name);
            foreach (var alias in catalog.AliasesFor(fact.Name)) Add(alias);
            void Add(string name)
            {
                names.Add((VariantKey(Key(name)), new(fact, true)));
            }
        }
        _names = names.GroupBy(n => n.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(n => n.Binding).Distinct().ToArray(), StringComparer.Ordinal);
        MaxNameLength = Math.Max(_exact.Keys.Select(k => k.Length).DefaultIfEmpty().Max(),
            _names.Keys.Select(k => k.Length).DefaultIfEmpty().Max())
            + catalog.SeriesDescriptors.Select(s => s.Length + 1).DefaultIfEmpty().Max() + 4;
    }

    public IReadOnlyList<Binding> Resolve(string value)
    {
        var key = Key(value);
        return _exact.GetValueOrDefault(key) ?? _names.GetValueOrDefault(VariantKey(key)) ?? [];
    }
    public IReadOnlyList<Binding> ResolveExact(string value) => _exact.GetValueOrDefault(Key(value)) ?? [];

    private string VariantKey(string key)
    {
        if (key.StartsWith("THE ", StringComparison.Ordinal)) key = key[4..];
        foreach (var tail in _descriptorSuffixes)
        {
            if (key.Length > tail.Length && key.EndsWith(tail, StringComparison.Ordinal))
            { key = key[..^tail.Length]; break; }
        }
        foreach (var pair in _wordPairs)
        {
            var plural = pair.Plural;
            if (key == plural) return pair.Singular;
            if (key.EndsWith(" " + plural, StringComparison.Ordinal))
                return key[..^plural.Length] + pair.Singular;
        }
        return key;
    }

    public static string Key(string value)
    {
        var text = ReleaseTextView.SceneWords(value).ToCharArray();
        for (var i = 0; i < text.Length; i++)
            if (text[i] is ':' or ',' || text[i] == '.' && !(i > 0 && i + 1 < text.Length
                && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))) text[i] = ' ';
        return IdentityText.Canonical(new string(text)).ToUpperInvariant();
    }
}
