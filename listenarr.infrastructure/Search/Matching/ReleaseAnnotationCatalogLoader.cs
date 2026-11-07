using System.Text.Json;
using System.Text.Json.Serialization;
using Listenarr.Application.Search.Matching;

namespace Listenarr.Infrastructure.Search.Matching;

public static class ReleaseAnnotationCatalogLoader
{
    public const int MaximumBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        MaxDepth = 16
    };

    public static ReleaseAnnotationCatalog LoadDefault()
    {
        using var stream = typeof(ReleaseAnnotationCatalogLoader).Assembly.GetManifestResourceStream(
            "Listenarr.Infrastructure.Search.Matching.release-annotations.json");
        return stream == null ? ReleaseAnnotationCatalog.Unavailable : Load(stream);
    }

    public static ReleaseAnnotationCatalog Load(Stream stream)
    {
        try
        {
            // Read at most the permitted resource size plus one byte, including nonseekable streams.
            using var buffer = new MemoryStream();
            var bytes = new byte[4096]; int count;
            while ((count = stream.Read(bytes, 0, Math.Min(bytes.Length, MaximumBytes + 1 - (int)buffer.Length))) > 0)
            {
                buffer.Write(bytes, 0, count);
                if (buffer.Length > MaximumBytes) return ReleaseAnnotationCatalog.Unavailable;
            }
            buffer.Position = 0;
            var data = JsonSerializer.Deserialize<CatalogData>(buffer, Options);
            return data == null ? ReleaseAnnotationCatalog.Unavailable : new(data.SchemaVersion, data.Labels,
                data.SeriesAliases, data.SeriesDescriptors, data.SeriesWordPairs, data.NameParticles, data.NameSuffixes, data.CollectiveCredits);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or NotSupportedException)
        {
            return ReleaseAnnotationCatalog.Unavailable;
        }
    }

    private sealed record CatalogData(int SchemaVersion, AnnotationLabel[] Labels, SeriesNameAlias[] SeriesAliases,
        string[] SeriesDescriptors, SeriesWordPair[] SeriesWordPairs, string[] NameParticles, string[] NameSuffixes, string[] CollectiveCredits);
}
