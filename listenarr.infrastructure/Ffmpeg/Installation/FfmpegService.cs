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
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// Resolves and runs ffprobe. The binary is provided by the Openur.FFprobeStatic NuGet package
    /// and restored at build time, so the version is pinned in Directory.Packages.props like any
    /// other dependency and nothing is downloaded where the package covers the platform. A binary
    /// in the configured ffmpeg directory is used only when no packaged binary matches the running
    /// platform; while a packaged one exists, a hand-placed or previously downloaded copy there is
    /// ignored, even if the packaged one later fails to run. With neither, startup falls back to
    /// the download in <c>FfmpegService.Installer.cs</c>.
    /// </summary>
    public partial class FfmpegService : IFfmpegService
    {
        // Must match the Openur.FFprobeStatic version in Directory.Packages.props; a test enforces it.
        internal const string PackagedVersion = "9.0.2.508";

        internal const string PackagedLicenseNotice =
            "ffprobe is a static FFmpeg build shipped with Listenarr in the Openur.FFprobeStatic "
            + PackagedVersion + " package. Review FFmpeg licensing (LGPL/GPL) at https://ffmpeg.org/legal.html";

        internal const string LegacyLicenseNotice =
            "ffprobe was found in the ffmpeg directory under the config directory and did not ship with Listenarr. "
            + "Its licence depends on where it came from; review FFmpeg licensing (LGPL/GPL) at https://ffmpeg.org/legal.html";

        private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        private readonly string _applicationBaseDirectory;
        private readonly IReadOnlyList<string> _runtimeIdentifiers;
        private readonly bool _isWindows;
        private readonly string _legacyDirectory;
        private readonly string _ffprobeExecutableName;
        private readonly string _legacyFfprobePath;
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
            IApplicationPathService applicationPathService,
            string? applicationBaseDirectory = null,
            string? runtimeIdentifier = null)
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
            _applicationBaseDirectory = applicationBaseDirectory ?? AppContext.BaseDirectory;
            _runtimeIdentifiers = runtimeIdentifier == null
                ? FfprobeBinaryLocator.CurrentRuntimeIdentifiers()
                : new[] { runtimeIdentifier }.Concat(FfprobeBinaryLocator.CurrentRuntimeIdentifiers()).Distinct(StringComparer.Ordinal).ToArray();
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            _legacyDirectory = applicationPathService.FfmpegRootPath;
            _ffprobeExecutableName = FfprobeBinaryLocator.ExecutableName(_isWindows);
            _legacyFfprobePath = Path.Join(_legacyDirectory, _ffprobeExecutableName);
        }

        /// <summary>
        /// The packaged binary first, then one in the configured ffmpeg directory. A binary an older
        /// release downloaded into that directory is therefore superseded by the packaged one.
        /// </summary>
        private string? ResolveFfprobePath()
        {
            var packaged = ResolvePackagedFfprobePath();
            if (packaged != null)
            {
                return packaged;
            }

            return File.Exists(_legacyFfprobePath) ? _legacyFfprobePath : null;
        }

        private string? ResolvePackagedFfprobePath()
        {
            return FfprobeBinaryLocator.Locate(_applicationBaseDirectory, _runtimeIdentifiers, _isWindows);
        }

        /// <summary>
        /// Return the ffprobe path if a packaged or configured binary exists. Never downloads; only
        /// <see cref="EnsureFfprobeInstalledAsync"/> falls back to the download.
        /// </summary>
        public Task<string?> GetFfprobePathAsync()
        {
            var path = ResolveFfprobePath();
            if (path != null)
            {
                _logger.LogInformation("Found ffprobe at {Path}", path);
            }
            else
            {
                _logger.LogInformation(
                    "No ffprobe found for runtimes {Rids} under {ApplicationBase} or {LegacyDirectory}",
                    string.Join(", ", _runtimeIdentifiers),
                    _applicationBaseDirectory,
                    _legacyDirectory);
            }

            return Task.FromResult(path);
        }

        /// <summary>
        /// Resolve ffprobe and make sure it can be executed, downloading it into the configured
        /// ffmpeg directory only when nothing is packaged for this platform and nothing is there
        /// yet. NuGet restore leaves
        /// the binary executable by its owner only (0766 less the umask), and an unzip tool or an
        /// artifact transfer can strip even that, so the execute bits are restored here when this
        /// process owns the file. A foreign-owned install cannot be changed from here, which is
        /// why the Docker image sets the mode at build time.
        /// </summary>
        public async Task<string?> EnsureFfprobeInstalledAsync()
        {
            var path = ResolveFfprobePath();
            if (path == null)
            {
                _logger.LogInformation(
                    "No packaged ffprobe for runtimes {Rids} and none at {LegacyDirectory}; falling back to the download",
                    string.Join(", ", _runtimeIdentifiers),
                    _legacyDirectory);
                return await DownloadFfprobeAsync();
            }

            if (!OperatingSystem.IsWindows())
            {
                EnsureExecutable(path);
            }

            return path;
        }

        [UnsupportedOSPlatform("windows")]
        private void EnsureExecutable(string path)
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & ExecuteBits) != ExecuteBits)
                {
                    File.SetUnixFileMode(path, mode | ExecuteBits);
                    _logger.LogInformation("Set execute permission on ffprobe at {Path}", path);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning(ex, "Could not set execute permission on ffprobe at {Path}", path);
            }
        }
    }
}
