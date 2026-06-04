/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Models;
using Listenarr.Application.Security;
using Listenarr.Domain.Models.Exceptions;

namespace Listenarr.Infrastructure.Ffmpeg
{
    public class FfmpegService : IFfmpegService
    {
        private readonly string _baseDir;
        private readonly string _ffprobeName;
        private readonly string _ffprobePath;
        private readonly ILogger<FfmpegService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IStartupConfigService _startupConfigService;
        private readonly IProcessRunner _processRunner;
        // Allow disabling auto-download via environment variable
        private readonly bool _autoInstall;

        public FfmpegService(
            ILogger<FfmpegService> logger,
            IStartupConfigService startupConfigService,
            IProcessRunner processRunner,
            IApplicationPathService applicationPathService)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            // Use a longer timeout than HttpClient's 100s default because static ffprobe archives
            // can be large and hosts may have slow links. Allow override via environment variable.
            var timeoutSeconds = 300;
            var timeoutEnv = Environment.GetEnvironmentVariable("LISTENARR_FFPROBE_DOWNLOAD_TIMEOUT_SECONDS");
            if (!string.IsNullOrWhiteSpace(timeoutEnv)
                && int.TryParse(timeoutEnv, out var parsedSeconds)
                && parsedSeconds > 0)
            {
                timeoutSeconds = parsedSeconds;
            }
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            _autoInstall = Environment.GetEnvironmentVariable("LISTENARR_AUTO_INSTALL_FFPROBE")?.ToLower() != "false"; // default true
            _startupConfigService = startupConfigService;
            _processRunner = processRunner;

            _baseDir = applicationPathService.FfmpegRootPath;
            _ffprobeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
            _ffprobePath = Path.Join(_baseDir, _ffprobeName);
        }

        private static async Task TryDeleteFileAsync(string path, int retries = 3, int delayMs = 100, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                {
                    if (i == retries - 1) return;
                    try { await Task.Delay(delayMs, cancellationToken); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private static bool TryBuildPathUnderRoot(string rootPath, string entryPath, out string resolvedPath)
        {
            resolvedPath = string.Empty;

            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(entryPath))
            {
                return false;
            }

            var normalizedRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var normalizedEntry = entryPath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .Trim();

            if (string.IsNullOrWhiteSpace(normalizedEntry))
            {
                return false;
            }

            normalizedEntry = normalizedEntry.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedEntry))
            {
                return false;
            }

            var candidatePath = Path.GetFullPath(
                normalizedRoot + Path.DirectorySeparatorChar + normalizedEntry);
            if (!candidatePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidatePath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolvedPath = candidatePath;
            return true;
        }

        /// <summary>
        /// Return the ffprobe path if it exists in the configured bundled directory. This method
        /// does NOT attempt to download or install ffprobe.
        /// </summary>
        public Task<string?> GetFfprobePathAsync()
        {
            if (File.Exists(_ffprobePath))
            {
                _logger.LogInformation("Found bundled ffprobe at {Path}", _ffprobePath);
                return Task.FromResult<string?>(_ffprobePath);
            }

            _logger.LogInformation("No bundled ffprobe found at {Path}", _ffprobePath);
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Ensure ffprobe is installed into the bundled directory. This performs the download
        /// and extraction flow when no bundled binary exists. Intended to be called once at startup.
        /// </summary>
        public async Task<string?> EnsureFfprobeInstalledAsync()
        {
            if (await GetFfprobePathAsync() != null)
            {
                return _ffprobePath;
            }

            if (!_autoInstall)
            {
                _logger.LogInformation("Auto-install of ffprobe is disabled via LISTENARR_AUTO_INSTALL_FFPROBE");
                return null;
            }

            Directory.CreateDirectory(_baseDir);

            try
            {
                // The original installation logic has been preserved here. It will only run when
                // EnsureFfprobeInstalledAsync is called (e.g., at program startup).
                string? downloadUrl = GetDownloadUrlForPlatform();
                string? discoveredChecksum = null;
                try
                {
                    var cfg = _startupConfigService.GetConfig();
                    if (cfg?.Ffmpeg?.Provider != null && cfg.Ffmpeg.Provider.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = cfg.Ffmpeg.Provider.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            var repo = parts[1];
                            var assetInfo = await TryDiscoverGithubAssetAsync(repo, cfg.Ffmpeg.ReleaseOverride, cfg.Ffmpeg.Arch);
                            if (!string.IsNullOrEmpty(assetInfo.assetUrl))
                            {
                                downloadUrl = assetInfo.assetUrl;
                                if (!string.IsNullOrEmpty(assetInfo.checksumContent))
                                {
                                    discoveredChecksum = assetInfo.checksumContent;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Error while reading startup ffmpeg provider config");
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    _logger.LogWarning("No ffprobe download URL configured for this platform");
                    return null;
                }

                _logger.LogInformation("Downloading ffprobe from {Url}", downloadUrl);

                using var resp = await _httpClient.GetAsync(downloadUrl);
                resp.EnsureSuccessStatusCode();

                var tmpFile = Path.Join(_baseDir, "ffprobe-download.tmp");
                await using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write))
                {
                    await resp.Content.CopyToAsync(fs);
                }

                // Compute SHA256 for logging / future verification
                string? computedHash = null;
                try
                {
                    using var sha = SHA256.Create();
                    await using var fs2 = File.OpenRead(tmpFile);
                    var hash = await sha.ComputeHashAsync(fs2);
                    var hashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    computedHash = hashHex;
                    _logger.LogInformation("Downloaded ffprobe archive SHA256={Hash}", hashHex);
                }
                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                { /* non-fatal */
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                var expected = GetChecksumForPlatform();
                if (string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(discoveredChecksum))
                {
                    var parsed = ParseChecksumFileForAsset(discoveredChecksum, Path.GetFileName(downloadUrl));
                    if (!string.IsNullOrEmpty(parsed)) expected = parsed;
                }

                if (string.IsNullOrEmpty(expected))
                {
                    try
                    {
                        var checksumFiles = Directory.GetFiles(_baseDir, "*checksum*", SearchOption.TopDirectoryOnly)
                            .Concat(Directory.GetFiles(_baseDir, "SHA256*", SearchOption.TopDirectoryOnly));
                        foreach (var cf in checksumFiles)
                        {
                            try
                            {
                                var content = await File.ReadAllTextAsync(cf);
                                var parsed = ParseChecksumFileForAsset(content, Path.GetFileName(downloadUrl));
                                if (!string.IsNullOrEmpty(parsed)) { expected = parsed; break; }
                            }
                            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                    }
                    catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                }

                if (!string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(computedHash) && !string.Equals(expected, computedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Downloaded ffprobe checksum mismatch (expected {Expected} != actual {Actual}). Aborting install.", expected, computedHash);
                    await TryDeleteFileAsync(tmpFile);
                    return null;
                }

                if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || downloadUrl.EndsWith(".ffmpeg.zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(tmpFile, new ReaderOptions());
                    var baseRoot = Path.GetFullPath(_baseDir);
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        var entryPath = entry.Key ?? string.Empty;
                        if (!TryBuildPathUnderRoot(baseRoot, entryPath, out var outPath))
                        {
                            _logger.LogWarning("Skipping archive entry outside extraction root: {Entry}", entryPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? _baseDir);
                        entry.WriteToFile(outPath, new ExtractionOptions() { ExtractFullPath = true, Overwrite = true });
                        _logger.LogDebug("Extracted archive entry to {OutPath}", outPath);
                    }
                    await TryDeleteFileAsync(tmpFile);
                }
                else if (downloadUrl.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) || downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || downloadUrl.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var stream = File.OpenRead(tmpFile);
                        var readerOptions = new ReaderOptions { LeaveStreamOpen = false };
                        using var reader = ReaderFactory.OpenReader(stream, readerOptions);
                        var baseRoot = Path.GetFullPath(_baseDir);
                        while (reader.MoveToNextEntry())
                        {
                            if (!reader.Entry.IsDirectory)
                            {
                                var entryPath = reader.Entry.Key ?? string.Empty;
                                if (!TryBuildPathUnderRoot(baseRoot, entryPath, out var outPath))
                                {
                                    _logger.LogWarning("Skipping archive entry outside extraction root: {Entry}", entryPath);
                                    continue;
                                }

                                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? _baseDir);
                                using var entryStream = reader.OpenEntryStream();
                                await using var outFs = File.Create(outPath);
                                await entryStream.CopyToAsync(outFs);
                                _logger.LogDebug("Extracted archive entry to {OutPath}", outPath);
                            }
                        }
                        await TryDeleteFileAsync(tmpFile);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Managed extraction failed for {Tmp}; skipping system tar fallback to avoid unsafe archive extraction", tmpFile);
                        await TryDeleteFileAsync(tmpFile);
                        return null;
                    }
                }
                else
                {
                    File.Move(tmpFile, _ffprobePath);
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var candidates = Directory.GetFiles(_baseDir, "ffprobe*", SearchOption.AllDirectories);
                        foreach (var cand in candidates)
                        {
                            try
                            {
                                var psiCh = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = "chmod",
                                    Arguments = $"+x \"{cand}\"",
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };

                                await _processRunner.RunAsync(psiCh, 3000);
                            }
                            catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException)
                            { /* best effort */
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                    }
                    catch (Exception caughtEx_7) when (caughtEx_7 is not OperationCanceledException && caughtEx_7 is not OutOfMemoryException && caughtEx_7 is not StackOverflowException)
                    { /* best effort */
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                }

                try
                {
                    // Find any extracted ffprobe candidates under the baseDir
                    var candidates = new List<string>();
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        candidates.AddRange(Directory.GetFiles(_baseDir, "ffprobe.exe", SearchOption.AllDirectories));
                    }
                    else
                    {
                        candidates.AddRange(Directory.GetFiles(_baseDir, "ffprobe", SearchOption.AllDirectories));
                        // include any file names that contain ffprobe (fallback)
                        candidates.AddRange(Directory.EnumerateFiles(_baseDir, "*ffprobe*", SearchOption.AllDirectories));
                    }

                    // Prefer exact filename matches, and prefer those located in a 'bin' directory
                    string? chosen = null;
                    var exactMatches = candidates.Where(p => string.Equals(Path.GetFileName(p), _ffprobeName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exactMatches.Any())
                    {
                        // Prefer candidates with '/bin/' in the path (common for ffmpeg archives)
                        chosen = exactMatches.FirstOrDefault(p => p.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
                                 ?? exactMatches.OrderBy(p => p.Length).FirstOrDefault();
                    }
                    else if (candidates.Any())
                    {
                        chosen = candidates.OrderBy(p => p.Length).First();
                    }

                    if (!string.IsNullOrEmpty(chosen))
                    {
                        var dest = _ffprobePath;
                        try
                        {
                            // Ensure destination directory exists
                            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? _baseDir);

                            var chosenFull = Path.GetFullPath(chosen);
                            var destFull = Path.GetFullPath(dest);

                            if (string.Equals(chosenFull, destFull, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation("ffprobe already extracted at destination {Dest}", destFull);
                            }
                            else
                            {
                                // If destination already exists, remove to allow move
                                if (File.Exists(dest))
                                {
                                    try { File.Delete(dest); }
                                    catch (Exception caughtEx_8) when (caughtEx_8 is not OperationCanceledException && caughtEx_8 is not OutOfMemoryException && caughtEx_8 is not StackOverflowException)
                                    { /* best-effort */
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }

                                // Attempt to move the file into the baseDir root. If move fails (cross-volume), fall back to copy.
                                try
                                {
                                    File.Move(chosen, dest);
                                    _logger.LogInformation("Moved ffprobe from {Src} to {Dest}", chosen, dest);
                                }
                                catch (Exception mvEx) when (mvEx is not OperationCanceledException && mvEx is not OutOfMemoryException && mvEx is not StackOverflowException)
                                {
                                    try
                                    {
                                        File.Copy(chosen, dest, overwrite: true);
                                        _logger.LogInformation("Copied ffprobe from {Src} to {Dest} (move failed: {Err})", chosen, dest, mvEx.Message);
                                    }
                                    catch (Exception cpEx) when (cpEx is not OperationCanceledException && cpEx is not OutOfMemoryException && cpEx is not StackOverflowException)
                                    {
                                        _logger.LogWarning(cpEx, "Failed to copy ffprobe from {Src} to {Dest}", chosen, dest);
                                    }
                                }
                            }

                            // Ensure executable bit on non-Windows
                            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(dest))
                            {
                                try
                                {
                                    var psiCh = new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = "chmod",
                                        Arguments = $"+x \"{dest}\"",
                                        RedirectStandardOutput = true,
                                        RedirectStandardError = true,
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                                    };

                                    await _processRunner.RunAsync(psiCh, 3000);
                                }
                                catch (Exception caughtEx_9) when (caughtEx_9 is not OperationCanceledException && caughtEx_9 is not OutOfMemoryException && caughtEx_9 is not StackOverflowException)
                                { /* best effort */
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to move or copy ffprobe candidate {Src} to {Dest}", chosen, _ffprobePath);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No ffprobe binary found in extracted files under {BaseDir}", _baseDir);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Non-fatal error while locating/copying ffprobe from extracted files");
                }

                if (!File.Exists(_ffprobePath))
                {
                    _logger.LogWarning("ffprobe install did not produce the expected binary at {Path}", _ffprobePath);
                    return null;
                }

                var licensePath = Path.Join(_baseDir, "LICENSE_NOTICE.txt");
                await File.WriteAllTextAsync(licensePath, "ffprobe binaries downloaded. Review FFmpeg licensing (LGPL/GPL) at https://ffmpeg.org/legal.html\nSource: " + downloadUrl + "\n");

                _logger.LogInformation("ffprobe installed to {Path}", _ffprobePath);
                return _ffprobePath;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "ffprobe download/install timed out or was canceled");
                return null;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "ffprobe download/install was canceled");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to download or install ffprobe");
                return null;
            }
        }

        private string? GetDownloadUrlForPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                {
                    return "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz";
                }

                // johnvansickle static build (x86_64)
                return "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // evermeet/ffmpeg provides static macOS builds (note: keep an eye on licensing)
                return "https://evermeet.cx/ffmpeg/ffmpeg-6.0.zip";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // gyan.dev builds
                return "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
            }

            return null;
        }

        private string? GetChecksumForPlatform()
        {
            // For production you should pin the checksums for each provider + archive
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return null; // placeholder - add SHA256 hex string
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return null;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return null;
            }

            return null;
        }

        private async Task<(string? assetUrl, string? checksumContent)> TryDiscoverGithubAssetAsync(string repo, string? releaseOverride, string? arch)
        {
            try
            {
                // Use GitHub Releases API: https://api.github.com/repos/{owner}/{repo}/releases
                var releasesUrl = $"https://api.github.com/repos/{repo}/releases";
                using var req = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
                req.Headers.Add("User-Agent", "Listenarr-Installer");
                using var resp = await _httpClient.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                var docs = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
                if (docs.ValueKind != System.Text.Json.JsonValueKind.Array) return (null, null);

                foreach (var release in docs.EnumerateArray())
                {
                    var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(releaseOverride) && !tag.Contains(releaseOverride, StringComparison.OrdinalIgnoreCase)) continue;
                    if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        string? checksumContent = null;
                        string? chosenUrl = null;
                        // First, attempt to find checksum asset(s)
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.GetProperty("name").GetString() ?? string.Empty;
                            var url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                            if (string.IsNullOrEmpty(url)) continue;
                            if (name.Contains("sha256", StringComparison.OrdinalIgnoreCase) || name.Contains("checksum", StringComparison.OrdinalIgnoreCase) || name.Contains("sha256sums", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var c = await (await _httpClient.GetAsync(url)).Content.ReadAsStringAsync();
                                    if (!string.IsNullOrEmpty(c)) checksumContent = c;
                                }
                                catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                            }
                        }

                        // Then find a matching asset for platform/arch
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.GetProperty("name").GetString() ?? string.Empty;
                            var url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                            if (string.IsNullOrEmpty(url)) continue;
                            if (!string.IsNullOrEmpty(arch) && !name.Contains(arch, StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                            {
                                chosenUrl = url;
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(chosenUrl)) return (chosenUrl, checksumContent);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "GitHub asset discovery failed for repo {Repo}", repo);
            }

            return (null, null);
        }

        private static string? ParseChecksumFileForAsset(string checksumFileContent, string assetFileName)
        {
            if (string.IsNullOrEmpty(checksumFileContent) || string.IsNullOrEmpty(assetFileName)) return null;
            using var sr = new StringReader(checksumFileContent);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                // Common formats: "<checksum>  <filename>" or "<checksum>  *<filename>" or "<checksum> <filename>"
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var possibleHash = parts[0].Trim();
                    var possibleName = parts[^1].Trim();
                    if (possibleName.StartsWith("*")) possibleName = possibleName[1..];
                    if (possibleName.Equals(assetFileName, StringComparison.OrdinalIgnoreCase) || possibleName.EndsWith(assetFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return possibleHash;
                    }
                }
                else
                {
                    // Some checksum files list "filename: hash" or JSON; do simple contains
                    if (trimmed.Contains(assetFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        var tokens = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 2)
                        {
                            var candidate = tokens[1].Trim();
                            var candidateToken = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                            if (!string.IsNullOrEmpty(candidateToken)) return candidateToken;
                        }
                    }
                }
            }

            return null;
        }

        public async Task<AudioMetadata> RunFfprobeAsync(string filePath)
        {
            var sanitizedFilePath = LogRedaction.SanitizeFilePath(filePath);
            JsonElement ffprobeData;
            try
            {
                _logger.LogInformation("Running bundled ffprobe at {Path} against file {File}", _ffprobePath, filePath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-v");
                startInfo.ArgumentList.Add("quiet");
                startInfo.ArgumentList.Add("-print_format");
                startInfo.ArgumentList.Add("json");
                startInfo.ArgumentList.Add("-show_format");
                startInfo.ArgumentList.Add("-show_streams");
                startInfo.ArgumentList.Add(filePath);

                var pr = await _processRunner.RunAsync(startInfo, 10000);
                _logger.LogInformation("ffprobe exit code {Code} for file {File}; stderr length={Len}", pr.ExitCode, sanitizedFilePath, pr.Stderr?.Length ?? 0);

                if (pr.ExitCode > 0)
                {
                    throw new FfmpegException($"ffprobe cannot read/process {sanitizedFilePath}");
                }

                if (string.IsNullOrEmpty(pr.Stdout))
                {
                    throw new FfmpegException($"Failed to parse ffprobe JSON output for {sanitizedFilePath}: Cannot retrieve output or retrieved empty output");
                }

                try
                {
                    ffprobeData = JsonSerializer.Deserialize<JsonElement>(pr.Stdout);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    throw new FfmpegException($"Failed to parse ffprobe JSON output for {sanitizedFilePath}", ex);
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new FfmpegException($"ffprobe execution failed for {sanitizedFilePath}", ex);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new FfmpegException($"Error running ffprobe for {sanitizedFilePath}", ex);
            }

            var metadata = new AudioMetadata();

            // Try to get format info
            if (ffprobeData.TryGetProperty("format", out var fmt))
            {
                if (fmt.TryGetProperty("duration", out var durEl)
                    && durEl.ValueKind == JsonValueKind.String
                    && double.TryParse(durEl.GetString(), out var dur))
                {
                    metadata.Duration = TimeSpan.FromSeconds(dur);
                }
                if (fmt.TryGetProperty("format_name", out var fmtName) && fmtName.ValueKind == JsonValueKind.String)
                {
                    var rawFmt = fmtName.GetString() ?? string.Empty;
                    var primary = rawFmt.Split(',')[0];

                    var ext = Path.GetExtension(filePath)?.TrimStart('.')?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(ext))
                    {
                        if (ext == "m4b")
                        {
                            metadata.Format = ext.ToUpperInvariant();
                            metadata.Container = ext.ToUpperInvariant();
                        }
                        else
                        {
                            metadata.Format = primary.ToUpperInvariant();
                            metadata.Container = primary.ToUpperInvariant();
                        }
                    }
                    else
                    {
                        metadata.Format = primary.ToUpperInvariant();
                        metadata.Container = primary.ToUpperInvariant();
                    }
                }
                if (fmt.TryGetProperty("bit_rate", out var br) && br.ValueKind == JsonValueKind.String && int.TryParse(br.GetString(), out var bitRate))
                {
                    metadata.BitRate = bitRate;
                }
                if (fmt.TryGetProperty("tags", out var formatTags) && formatTags.ValueKind == JsonValueKind.Object)
                {
                    ApplyTagMetadata(metadata, formatTags);
                }
            }

            // Streams: look for audio stream for sample rate, channels
            if (ffprobeData.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams
                    .EnumerateArray()
                    .Where(s => s.TryGetProperty("codec_type", out var codecType) && codecType.GetString() == "audio"))
                {
                    if (s.TryGetProperty("sample_rate", out var sr) && sr.ValueKind == JsonValueKind.String && int.TryParse(sr.GetString(), out var sampleRate))
                    {
                        metadata.SampleRate = sampleRate;
                    }
                    if (s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number)
                    {
                        metadata.Channels = ch.GetInt32();
                    }
                    if (s.TryGetProperty("bit_rate", out var sbr) && sbr.ValueKind == JsonValueKind.String && int.TryParse(sbr.GetString(), out var sbit))
                    {
                        metadata.BitRate = metadata.BitRate == 0 ? sbit : metadata.BitRate;
                    }
                    if (s.TryGetProperty("codec_name", out var codecName) && codecName.ValueKind == JsonValueKind.String)
                    {
                        metadata.Codec = codecName.GetString();
                    }
                    if (s.TryGetProperty("tags", out var streamTags) && streamTags.ValueKind == JsonValueKind.Object)
                    {
                        ApplyTagMetadata(metadata, streamTags);
                    }
                    break;
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(metadata.Title)) metadata.Title = fileName;
            if (string.IsNullOrEmpty(metadata.Format)) metadata.Format = Path.GetExtension(filePath).TrimStart('.').ToUpper();
            if (string.IsNullOrEmpty(metadata.Container)) metadata.Container = Path.GetExtension(filePath).TrimStart('.').ToUpper();

            _logger.LogInformation("Extracted ffprobe metadata from file: {File}", LogRedaction.SanitizeText(filePath));
            _logger.LogDebug("Parsed metadata: Duration={Duration} seconds, Format={Format}, Bitrate={Bitrate}, SampleRate={SampleRate}, Channels={Channels}", metadata.Duration.TotalSeconds, metadata.Format, metadata.BitRate, metadata.SampleRate, metadata.Channels);

            return metadata;
        }

        private static void ApplyTagMetadata(AudioMetadata metadata, JsonElement tags)
        {
            metadata.Title = FirstNonEmpty(metadata.Title, GetTag(tags, "title", "TITLE"));
            metadata.Artist = FirstNonEmpty(metadata.Artist, GetTag(tags, "artist", "ARTIST"));
            metadata.Album = FirstNonEmpty(metadata.Album, GetTag(tags, "album", "ALBUM"));
            metadata.AlbumArtist = FirstNonEmpty(metadata.AlbumArtist, GetTag(tags, "album_artist", "ALBUM_ARTIST", "album artist"));

            metadata.TrackNumber ??= ParseNumericTag(tags, "track", "TRACK", "tracknumber", "TRACKNUMBER");
            metadata.DiscNumber ??= ParseNumericTag(tags, "disc", "DISC", "discnumber", "DISCNUMBER");
            metadata.Year ??= ParseNumericTag(tags, "date", "DATE", "year", "YEAR");
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
            {
                return candidate!;
            }

            return string.Empty;
        }

        private static string? GetTag(JsonElement tags, params string[] names)
        {
            return names
                .Select(name => TryGetTagValue(tags, name, out var value) ? value : null)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                ?.Trim();
        }

        private static int? ParseNumericTag(JsonElement tags, params string[] names)
        {
            var raw = GetTag(tags, names);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var token = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? raw;
            var match = System.Text.RegularExpressions.Regex.Match(token, @"\d+");
            return match.Success && int.TryParse(match.Value, out var parsed) ? parsed : null;
        }

        private static bool TryGetTagValue(JsonElement tags, string name, out string? value)
        {
            if (tags.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                value = direct.GetString();
                return true;
            }

            foreach (var property in tags.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString();
                    return true;
                }
            }

            value = null;
            return false;
        }

        public string FfprobePath
        {
            get
            {
                return _ffprobePath;
            }
        }

        public async Task<string> GetLicenseAsync()
        {
            var licensePath = Path.Join(_baseDir, "LICENSE_NOTICE.txt");
            if (System.IO.File.Exists(licensePath))
            {
                return await System.IO.File.ReadAllTextAsync(licensePath);
            }

            return string.Empty;
        }
    }
}
