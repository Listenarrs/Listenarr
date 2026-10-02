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
using System.Runtime.InteropServices;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    public partial class FfmpegService : IFfmpegService
    {
        private readonly string _baseDir;
        private readonly string _ffprobeName;
        private readonly string _ffprobePath;
        private readonly ILogger<FfmpegService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IStartupConfigService _startupConfigService;
        private readonly IProcessRunner _processRunner;
        private readonly FfprobeGithubAssetDiscoverer _githubAssetDiscoverer;
        // Allow disabling auto-download via environment variable
        private readonly bool _autoInstall;

        public FfmpegService(
            ILogger<FfmpegService> logger,
            HttpClient httpClient,
            IStartupConfigService startupConfigService,
            IProcessRunner processRunner,
            IApplicationPathService applicationPathService)
        {
            _logger = logger;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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
            _githubAssetDiscoverer = new FfprobeGithubAssetDiscoverer(_httpClient, _logger);
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
                            var assetInfo = await _githubAssetDiscoverer.TryDiscoverAsync(repo, cfg.Ffmpeg.ReleaseOverride, cfg.Ffmpeg.Arch);
                            if (!string.IsNullOrEmpty(assetInfo.AssetUrl))
                            {
                                downloadUrl = assetInfo.AssetUrl;
                                if (!string.IsNullOrEmpty(assetInfo.ChecksumContent))
                                {
                                    discoveredChecksum = assetInfo.ChecksumContent;
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
                if (!FileSystemSafety.TryValidateMutationTarget(tmpFile, [_baseDir], out tmpFile, out var tmpReason))
                {
                    _logger.LogWarning("Blocked ffprobe download temp path: {Reason}", LogRedaction.SanitizeText(tmpReason));
                    return null;
                }

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
                {
                    _logger.LogDebug(caughtEx_2, "Could not compute the SHA256 of the downloaded ffprobe archive; continuing without a computed hash");
                }

                var expected = GetChecksumForPlatform();
                if (string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(discoveredChecksum))
                {
                    var parsed = FfprobeChecksumParser.ParseForAsset(discoveredChecksum, Path.GetFileName(downloadUrl));
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
                                var parsed = FfprobeChecksumParser.ParseForAsset(content, Path.GetFileName(downloadUrl));
                                if (!string.IsNullOrEmpty(parsed)) { expected = parsed; break; }
                            }
                            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                            {
                                _logger.LogDebug(caughtEx_3, "Could not read checksum file {ChecksumFile}; trying the next one", cf);
                            }
                        }
                    }
                    catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
                    {
                        _logger.LogDebug(caughtEx_4, "Could not enumerate checksum files in the ffprobe base directory; continuing without a local checksum");
                    }
                }

                if (!string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(computedHash) && !string.Equals(expected, computedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Downloaded ffprobe checksum mismatch (expected {Expected} != actual {Actual}). Aborting install.", expected, computedHash);
                    await TryDeleteFileAsync(tmpFile);
                    return null;
                }

                if (!await FfprobeArchiveExtractor.ExtractAsync(downloadUrl, tmpFile, _baseDir, _ffprobePath, _logger))
                {
                    return null;
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
                            {
                                _logger.LogDebug(caughtEx_6, "Could not mark extracted candidate {Candidate} executable", cand);
                            }
                        }
                    }
                    catch (Exception caughtEx_7) when (caughtEx_7 is not OperationCanceledException && caughtEx_7 is not OutOfMemoryException && caughtEx_7 is not StackOverflowException)
                    {
                        _logger.LogDebug(caughtEx_7, "Could not walk the extracted ffprobe directory to fix permissions");
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
                            if (!FileSystemSafety.TryValidateMutationTarget(chosenFull, [_baseDir], out chosenFull, out var chosenReason))
                            {
                                _logger.LogWarning(
                                    "Blocked ffprobe candidate move. Candidate reason: {CandidateReason}",
                                    LogRedaction.SanitizeText(chosenReason));
                                return null;
                            }

                            if (!FileSystemSafety.TryValidateMutationTarget(destFull, [_baseDir], out destFull, out var destReason))
                            {
                                _logger.LogWarning(
                                    "Blocked ffprobe candidate move. Destination reason: {DestinationReason}",
                                    LogRedaction.SanitizeText(destReason));
                                return null;
                            }
                            chosen = chosenFull;
                            dest = destFull;

                            if (FileUtils.AreFilesystemPathsEquivalentForCurrentOs(chosenFull, destFull))
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
                                    {
                                        _logger.LogDebug(caughtEx_8, "Could not delete the existing file at {Destination} before moving ffprobe into place", dest);
                                    }
                                }

                                // Attempt to move the file into the baseDir root. If move fails (cross-volume), fall back to copy.
                                try
                                {
                                    File.Move(chosenFull, destFull);
                                    _logger.LogInformation("Moved ffprobe from {Src} to {Dest}", chosenFull, destFull);
                                }
                                catch (Exception mvEx) when (mvEx is not OperationCanceledException && mvEx is not OutOfMemoryException && mvEx is not StackOverflowException)
                                {
                                    try
                                    {
                                        File.Copy(chosenFull, destFull, overwrite: true);
                                        _logger.LogInformation("Copied ffprobe from {Src} to {Dest} (move failed: {Err})", chosenFull, destFull, mvEx.Message);
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
                                {
                                    _logger.LogDebug(caughtEx_9, "Could not mark {Destination} executable after installing ffprobe", dest);
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
            return FfprobePlatformDefaults.GetDownloadUrl();
        }

        private string? GetChecksumForPlatform()
        {
            return FfprobePlatformDefaults.GetChecksum();
        }

    }
}
