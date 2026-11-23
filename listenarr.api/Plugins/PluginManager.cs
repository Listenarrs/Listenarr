using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Plugins;

/// <summary>
/// Runtime plugin management: list installed/available plugins, manage repositories
/// (URLs to a <c>listenarr-plugins.json</c> index), and install/uninstall plugin packages.
/// Filesystem-only — the startup <see cref="PluginLoader"/> / <see cref="PluginAssets"/> apply
/// the change on the next boot, so install/uninstall are followed by a restart. Generic;
/// knows nothing about any specific plugin.
/// </summary>
public sealed class PluginManager
{
    private const long MaxDownloadBytes = 64L * 1024 * 1024;       // 64 MB compressed
    private const long MaxUncompressedBytes = 256L * 1024 * 1024;  // 256 MB extracted

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _pluginsDir;
    private readonly string _reposFile;
    private readonly ILogger<PluginManager> _logger;

    public PluginManager(IHttpClientFactory httpFactory, string pluginsDir, string configDir, ILogger<PluginManager> logger)
    {
        _httpFactory = httpFactory;
        _pluginsDir = pluginsDir;
        _reposFile = Path.Combine(configDir, "plugin-repositories.json");
        _logger = logger;
    }

    public sealed record RepoPlugin(string Id, string? Name, string? Version, string? Description, string? Author, string? Package);
    public sealed record RepoIndex(string? Name, RepoPlugin[]? Plugins);
    public sealed record PluginInfo(string Id, string Name, string Version, string? Description, string? Author,
        string Status, string? AvailableVersion, string? Repository);

    // --- repositories ---
    public List<string> GetRepositories()
    {
        if (!File.Exists(_reposFile))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_reposFile), Json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private void SaveRepositories(List<string> repos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reposFile)!);
        File.WriteAllText(_reposFile, JsonSerializer.Serialize(repos, Json));
    }

    public async Task AddRepositoryAsync(string url, CancellationToken ct)
    {
        url = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Repository URL must be an absolute http(s) URL.");
        }

        // Validate it points at a parseable index before persisting.
        _ = await FetchIndexAsync(url, ct) ?? throw new ArgumentException("URL did not return a valid plugin index.");

        var repos = GetRepositories();
        if (!repos.Contains(url))
        {
            repos.Add(url);
            SaveRepositories(repos);
        }
    }

    public void RemoveRepository(string url)
    {
        var repos = GetRepositories();
        if (repos.Remove((url ?? string.Empty).Trim()))
        {
            SaveRepositories(repos);
        }
    }

    private async Task<RepoIndex?> FetchIndexAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            var json = await http.GetStringAsync(url, ct);
            var index = JsonSerializer.Deserialize<RepoIndex>(json, Json);
            return index?.Plugins is null ? null : index;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch plugin index {Url}", url);
            return null;
        }
    }

    // --- listing ---
    public Dictionary<string, (string Name, string Version)> GetInstalled()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_pluginsDir))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(_pluginsDir))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var m = JsonSerializer.Deserialize<RepoPlugin>(File.ReadAllText(manifestPath), Json);
                if (!string.IsNullOrWhiteSpace(m?.Id))
                {
                    result[m!.Id] = (m.Name ?? m.Id, m.Version ?? "0.0.0");
                }
            }
            catch
            {
                // Ignore an unreadable manifest — a half-written package shouldn't break the list.
            }
        }

        return result;
    }

    public async Task<List<PluginInfo>> ListAsync(CancellationToken ct)
    {
        var installed = GetInstalled();
        var byId = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var repo in GetRepositories())
        {
            var index = await FetchIndexAsync(repo, ct);
            if (index?.Plugins is null)
            {
                continue;
            }

            foreach (var p in index.Plugins)
            {
                if (string.IsNullOrWhiteSpace(p.Id) || byId.ContainsKey(p.Id))
                {
                    continue;
                }

                var isInstalled = installed.TryGetValue(p.Id, out var inst);
                var repoVersion = p.Version ?? "0.0.0";
                var status = !isInstalled
                    ? "available"
                    : VersionGreater(repoVersion, inst.Version) ? "update" : "installed";
                byId[p.Id] = new PluginInfo(
                    p.Id, p.Name ?? p.Id, isInstalled ? inst.Version : repoVersion,
                    p.Description, p.Author, status, isInstalled ? repoVersion : null, repo);
            }
        }

        // Installed plugins not offered by any repository (e.g. baked into the image).
        foreach (var (id, inst) in installed)
        {
            if (!byId.ContainsKey(id))
            {
                byId[id] = new PluginInfo(id, inst.Name, inst.Version, null, null, "installed", null, null);
            }
        }

        return byId.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool VersionGreater(string? a, string? b)
        => Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) && va > vb;

    // --- install / uninstall ---
    public async Task InstallAsync(string id, CancellationToken ct)
    {
        id = SanitizeId(id);

        string? package = null;
        foreach (var repo in GetRepositories())
        {
            var index = await FetchIndexAsync(repo, ct);
            var match = index?.Plugins?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                package = match.Package;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(package))
        {
            throw new InvalidOperationException($"No repository offers plugin '{id}'.");
        }

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        using var resp = await http.GetAsync(package, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException("Plugin package exceeds the size limit.");
        }

        var tmpZip = Path.Combine(Path.GetTempPath(), $"listenarr-plugin-{id}-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(_pluginsDir, id + ".staging");
        try
        {
            await using (var fs = File.Create(tmpZip))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            SafeExtract(tmpZip, staging);

            // The package must declare the same id we asked to install.
            var manifestPath = Path.Combine(staging, "plugin.json");
            var manifest = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<RepoPlugin>(File.ReadAllText(manifestPath), Json)
                : null;
            if (string.IsNullOrWhiteSpace(manifest?.Id) || !string.Equals(manifest!.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Package plugin.json id does not match the requested plugin id.");
            }

            var target = Path.Combine(_pluginsDir, id);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }

            Directory.Move(staging, target);
            _logger.LogInformation("Installed plugin {Id} v{Version}", id, manifest.Version);
        }
        finally
        {
            if (File.Exists(tmpZip))
            {
                File.Delete(tmpZip);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public void Uninstall(string id)
    {
        id = SanitizeId(id);
        var target = Path.Combine(_pluginsDir, id);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
            _logger.LogInformation("Uninstalled plugin {Id}", id);
        }
    }

    private static string SanitizeId(string id)
    {
        id = (id ?? string.Empty).Trim();
        if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new ArgumentException("Invalid plugin id.");
        }

        return id;
    }

    /// <summary>
    /// Extract <paramref name="zipPath"/> into <paramref name="destDir"/>, rejecting any entry that
    /// would escape the destination (zip-slip) and capping total uncompressed size. Static + pure so
    /// it can be unit-tested without a host.
    /// </summary>
    public static void SafeExtract(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var destRoot = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);

        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var targetPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!targetPath.StartsWith(destRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Zip entry escapes target directory: {entry.FullName}");
            }

            // Directory entry.
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            total += entry.Length;
            if (total > MaxUncompressedBytes)
            {
                throw new InvalidOperationException("Plugin package uncompressed size exceeds the limit.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }
}
