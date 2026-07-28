using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Listenarr.Infrastructure.DownloadClients.Slskd;

internal static partial class SlskdRequestBuilder
{
    // Portable default for Compose examples. Native and Windows deployments may
    // configure another absolute path in Listenarr's own runtime namespace.
    public const string NativeDownloadRoot = "/slskd-downloads";
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma"
    };

    public static Uri BuildBaseUri(DownloadClientConfiguration client)
    {
        if (string.IsNullOrWhiteSpace(client.Host))
            throw new ArgumentException("slskd host is required.", nameof(client));
        var scheme = client.UseSSL ? "https" : "http";
        var host = client.Host.Trim();
        if (host.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(host, UriKind.Absolute, out var supplied) || !string.IsNullOrEmpty(supplied.Query) || !string.IsNullOrEmpty(supplied.Fragment))
                throw new ArgumentException("slskd host must be an absolute host without query or fragment.", nameof(client));
            return new UriBuilder(supplied.Scheme, supplied.Host, client.Port > 0 ? client.Port : supplied.Port).Uri;
        }
        return new UriBuilder(scheme, host, client.Port > 0 ? client.Port : -1).Uri;
    }

    public static void ApplyOptionalApiKey(HttpClient http, DownloadClientConfiguration client)
    {
        if (client.Settings.TryGetValue("apiKey", out var value) && value?.ToString() is { Length: > 0 } apiKey)
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);
    }

    public static string GetListenarrVisibleSourceRoot(DownloadClientConfiguration client)
    {
        var configured = client.Settings.TryGetValue("listenarrSourceRoot", out var value)
            ? value?.ToString()?.Trim()
            : null;
        var root = string.IsNullOrWhiteSpace(configured) ? NativeDownloadRoot : configured;
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("slskd listenarrSourceRoot must be an absolute path in Listenarr's runtime namespace.", nameof(client));
        return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string BuildDestination(string audiobookId)
    {
        if (string.IsNullOrWhiteSpace(audiobookId) || !SafeIdRegex().IsMatch(audiobookId))
            throw new ArgumentException("Audiobook ID must contain only letters, digits, underscore, or hyphen.", nameof(audiobookId));
        return $"listenarr/{audiobookId}";
    }

    public static bool IsSafeAudioFile(string? filename, long size, string? extension = null)
    {
        if (size <= 0 || string.IsNullOrWhiteSpace(filename) ||
            filename.StartsWith("/", StringComparison.Ordinal) || filename.StartsWith("\\", StringComparison.Ordinal))
            return false;
        // Soulseek remote paths use backslashes. Normalize for validation only, while
        // retaining the original server-returned filename for the slskd batch API.
        var normalized = filename.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.Contains(':')))
            return false;

        // slskd 0.26 returns its candidate extension separately (for example "mp3")
        // while Filename may not contain a suffix.  Preserve the exact returned Filename
        // for the batch request, but use the separately-returned extension for validation.
        var suffix = Path.GetExtension(parts[^1]);
        if (string.IsNullOrWhiteSpace(suffix) && !string.IsNullOrWhiteSpace(extension))
            suffix = extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
        return AudioExtensions.Contains(suffix);
    }

    public static HttpRequestMessage BuildBatchRequest(string audiobookId, string username, IReadOnlyCollection<SlskdRemoteFile> files)
    {
        var destination = BuildDestination(audiobookId);
        if (string.IsNullOrWhiteSpace(username) || files.Count == 0 ||
            files.Any(file => !IsSafeAudioFile(file.Filename, file.Size, file.Extension)))
            throw new ArgumentException("The slskd batch request contains unsafe data.");

        return new HttpRequestMessage(HttpMethod.Post, "/api/v0/transfers/downloads/batches")
        {
            // Send the exact server-returned names/sizes; Extension is metadata used only for
            // safety validation and is not part of slskd's batch request schema.
            Content = JsonContent.Create(new
            {
                username,
                files = files.Select(file => new { filename = file.Filename, size = file.Size }).ToArray(),
                options = new { destination }
            })
        };
    }

    public static IReadOnlyList<string> MapCompletedFiles(string sourceRoot, string destination, IEnumerable<SlskdRemoteFile>? files)
    {
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var safeDestination = BuildDestination(destination.Split('/').Last());
        if (!string.Equals(destination, safeDestination, StringComparison.Ordinal))
            return [];

        return (files ?? [])
            .Where(file => IsSafeAudioFile(file.Filename, file.Size))
            // slskd moves a completed transfer into the batch destination using
            // the remote filename's basename; remote directory components are not
            // a local directory contract and must never be replayed into imports.
            .Select(file => Path.GetFullPath(Path.Combine(root, safeDestination.Replace('/', Path.DirectorySeparatorChar), Path.GetFileName(file.Filename.Replace('\\', '/')))))
            .Where(path => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdRegex();
}
