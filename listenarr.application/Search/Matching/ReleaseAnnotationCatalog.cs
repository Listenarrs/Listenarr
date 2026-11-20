using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Listenarr.Application.Search.Matching;

[Flags]
public enum AnnotationPlacement { Suffix = 1, TechnicalSuffix = 2, BracketedSuffix = 4, DelimitedPrefix = 8 }
public enum AnnotationKind { Group, Source }
public sealed record AnnotationLabel(string Id, string Name, AnnotationKind Kind, AnnotationPlacement Placements);
public sealed record SeriesNameAlias(string Canonical, string Alias);
public sealed record SeriesWordPair(string Singular, string Plural);

/// <summary>Validated immutable policy data. Construction does no file or network I/O.</summary>
public sealed class ReleaseAnnotationCatalog
{
    public static ReleaseAnnotationCatalog Unavailable { get; } = new();
    private readonly FrozenDictionary<string, AnnotationLabel> _labels;
    private readonly FrozenDictionary<string, IReadOnlyList<string>> _aliases;
    public bool IsAvailable { get; }
    public string Fingerprint { get; } = "catalog-unavailable";
    public IReadOnlyList<string> SeriesDescriptors { get; } = [];
    public IReadOnlyList<SeriesWordPair> SeriesWordPairs { get; } = [];
    public IReadOnlySet<string> NameParticles { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> NameSuffixes { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> CollectiveCredits { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private ReleaseAnnotationCatalog()
    {
        _labels = new Dictionary<string, AnnotationLabel>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, IReadOnlyList<string>>().ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ReleaseAnnotationCatalog(int schemaVersion, IReadOnlyList<AnnotationLabel> labels,
        IReadOnlyList<SeriesNameAlias> aliases, IReadOnlyList<string> descriptors,
        IReadOnlyList<SeriesWordPair> wordPairs, IReadOnlyList<string> particles,
        IReadOnlyList<string> suffixes, IReadOnlyList<string> collectiveCredits)
    {
        if (schemaVersion != 1 || labels is null || aliases is null || descriptors is null || wordPairs is null
            || particles is null || suffixes is null || collectiveCredits is null
            || labels.Count > 256 || aliases.Count > 128 || descriptors.Count > 16 || wordPairs.Count > 16
            || particles.Count > 32 || suffixes.Count > 16 || collectiveCredits.Count > 16)
            throw new ArgumentException("Invalid annotation catalog schema or budget");
        static string Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit
            && !value.Any(char.IsControl) ? IdentityText.Canonical(value) : throw new ArgumentException("Invalid catalog text");
        var normalized = labels.Select(l => l is null ? throw new ArgumentException("Null annotation") : new AnnotationLabel(
            Text(l.Id, 80), Text(l.Name, 64), l.Kind, l.Placements)).OrderBy(l => l.Id, StringComparer.Ordinal).ToArray();
        if (normalized.Any(l => !Enum.IsDefined(l.Kind) || l.Placements == 0 || ((int)l.Placements & ~15) != 0
                || l.Kind == AnnotationKind.Source && l.Placements != AnnotationPlacement.BracketedSuffix
                || l.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            || normalized.Select(l => l.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length
            || normalized.Select(l => Key(l.Name)).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Conflicting catalog annotations");
        _labels = normalized.ToFrozenDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var namedAliases = aliases.Select(a => a is null ? throw new ArgumentException("Null series alias")
            : new SeriesNameAlias(Text(a.Canonical, 256), Text(a.Alias, 256)))
            .OrderBy(a => Key(a.Canonical), StringComparer.Ordinal).ThenBy(a => Key(a.Alias), StringComparer.Ordinal).ToArray();
        if (namedAliases.Select(a => (Key(a.Canonical), Key(a.Alias))).Distinct().Count() != namedAliases.Length
            || namedAliases.GroupBy(a => ReleaseSeriesCatalog.Key(a.Canonical)).Any(g => g.Count() > 7))
            throw new ArgumentException("Duplicate series alias");
        _aliases = namedAliases.GroupBy(a => ReleaseSeriesCatalog.Key(a.Canonical), StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<string>)Array.AsReadOnly(g.Select(a => a.Alias).ToArray()), StringComparer.Ordinal);
        string[] Words(IReadOnlyList<string> words, int limit) => words.Select(w => Text(w, limit))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w, StringComparer.Ordinal).ToArray();
        SeriesDescriptors = Array.AsReadOnly(Words(descriptors, 32));
        SeriesWordPairs = Array.AsReadOnly(wordPairs.Select(p => p is null ? throw new ArgumentException("Null word pair")
            : new SeriesWordPair(Text(p.Singular, 32), Text(p.Plural, 32)))
            .OrderBy(p => p.Singular, StringComparer.Ordinal).ThenBy(p => p.Plural, StringComparer.Ordinal).ToArray());
        NameParticles = Words(particles, 32).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        NameSuffixes = Words(suffixes, 16).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        CollectiveCredits = Words(collectiveCredits, 64).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion,
            Labels = normalized,
            Aliases = namedAliases,
            SeriesDescriptors,
            SeriesWordPairs,
            Particles = NameParticles.Order(StringComparer.Ordinal).ToArray(),
            Suffixes = NameSuffixes.Order(StringComparer.Ordinal).ToArray(),
            Collective = CollectiveCredits.Order(StringComparer.Ordinal).ToArray()
        });
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        IsAvailable = true;
    }

    // Callers supply canonical comparison fields, never unnormalized raw release text.
    internal AnnotationLabel? Find(string name) => _labels.GetValueOrDefault(name);
    internal IEnumerable<string> AliasesFor(string series) => _aliases.GetValueOrDefault(ReleaseSeriesCatalog.Key(series)) ?? [];
    private static string Key(string value) => IdentityText.Canonical(value).ToUpperInvariant();
}
