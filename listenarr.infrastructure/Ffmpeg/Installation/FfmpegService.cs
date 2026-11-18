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
    /// other dependency and nothing is downloaded at runtime. A binary in the configured ffmpeg
    /// directory is used only when no packaged binary matches the running platform; while a
    /// packaged one exists, a hand-placed or previously downloaded copy there is ignored, even if
    /// the packaged one later fails to run. With neither, ffprobe is unavailable.
    /// </summary>
    public partial class FfmpegService : IFfmpegService
    {
        /// <summary>
        /// The notice for the packaged binary. The version is the one the binary reports through
        /// <c>-version</c>, so a package bump needs no code change; it is left out when the
        /// binary does not pass the check. Every build and publish output carries the licence texts
        /// and the source notice under <c>licenses/ffprobe/</c>.
        /// </summary>
        internal static string PackagedLicenseNotice(string? version)
        {
            var name = string.IsNullOrEmpty(version) ? "ffprobe" : $"ffprobe {version}";
            return name + " is a static FFmpeg build shipped with Listenarr in the Openur.FFprobeStatic package, "
                + "under the LGPL version 3 or later. The licence texts and a notice saying where its source is "
                + "are in licenses/ffprobe/ in the Listenarr application directory.";
        }

        internal const string LegacyLicenseNotice =
            "ffprobe was found in the ffmpeg directory under the config directory and did not ship with Listenarr. "
            + "Its licence depends on where it came from; review FFmpeg licensing (LGPL/GPL) at https://ffmpeg.org/legal.html";

        private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        private readonly string _applicationBaseDirectory;
        private readonly IReadOnlyList<string> _runtimeIdentifiers;
        private readonly bool _isWindows;
        private readonly string _legacyDirectory;
        private readonly string _legacyFfprobePath;
        private readonly ILogger<FfmpegService> _logger;
        private readonly IProcessRunner _processRunner;
        private readonly TimeProvider _timeProvider;

        public FfmpegService(
            ILogger<FfmpegService> logger,
            IProcessRunner processRunner,
            IApplicationPathService applicationPathService,
            string? applicationBaseDirectory = null,
            string? runtimeIdentifier = null,
            TimeProvider? timeProvider = null)
        {
            _logger = logger;
            _processRunner = processRunner;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _applicationBaseDirectory = applicationBaseDirectory ?? AppContext.BaseDirectory;
            _runtimeIdentifiers = runtimeIdentifier == null
                ? FfprobeBinaryLocator.CurrentRuntimeIdentifiers()
                : new[] { runtimeIdentifier }.Concat(FfprobeBinaryLocator.CurrentRuntimeIdentifiers()).Distinct(StringComparer.Ordinal).ToArray();
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            _legacyDirectory = applicationPathService.FfmpegRootPath;
            _legacyFfprobePath = Path.Join(_legacyDirectory, FfprobeBinaryLocator.ExecutableName(_isWindows));
        }

        /// <summary>
        /// The packaged binary first, then one in the configured ffmpeg directory. A binary an older
        /// release downloaded into that directory is therefore superseded by the packaged one. A
        /// chosen path that leads through a link out of the application or ffmpeg directory it sits
        /// in is rejected before anything chmods or runs it, and a rejected packaged binary does not
        /// fall back to the copy in the ffmpeg directory.
        /// </summary>
        private string? ResolveFfprobePath()
        {
            var path = ResolvePackagedFfprobePath()
                ?? (File.Exists(_legacyFfprobePath) ? _legacyFfprobePath : null);
            if (path != null && !FileSystemSafety.TryValidateMutationTarget(path, [_applicationBaseDirectory, _legacyDirectory], out _, out var reason))
            {
                _logger.LogWarning("Rejected ffprobe path outside configured roots: {Reason}", LogRedaction.SanitizeText(reason));
                return null;
            }

            return path;
        }

        private string? ResolvePackagedFfprobePath()
        {
            return FfprobeBinaryLocator.Locate(_applicationBaseDirectory, _runtimeIdentifiers, _isWindows);
        }

        /// <summary>
        /// Return the ffprobe path if a packaged or configured binary exists and passes the version
        /// check. Never downloads anything.
        /// </summary>
        public async Task<string?> GetFfprobePathAsync()
        {
            var (path, failure) = await ResolveValidatedFfprobePathAsync();
            if (path != null)
            {
                _logger.LogInformation("Found ffprobe at {Path}", path);
            }
            else if (failure == null)
            {
                _logger.LogInformation(
                    "No ffprobe found for runtimes {Rids} under {ApplicationBase} or {LegacyDirectory}",
                    string.Join(", ", _runtimeIdentifiers),
                    _applicationBaseDirectory,
                    _legacyDirectory);
            }

            return path;
        }

        /// <summary>
        /// Resolve ffprobe, make sure it can be executed and check it with <c>-version</c>. Nothing
        /// is downloaded, so with no packaged or configured binary ffprobe stays unavailable and
        /// audio metadata extraction is disabled. NuGet restore leaves the binary executable by
        /// its owner only (0766 less the umask), and an unzip tool or an artifact transfer can
        /// strip even that, so the execute bits are restored here when this process owns the file.
        /// A foreign-owned install cannot be changed from here, which is why the Docker image sets
        /// the mode at build time. A binary that fails the check is reported as unavailable; a
        /// failing packaged binary does not fall back to a copy in the ffmpeg directory.
        /// </summary>
        public async Task<string?> EnsureFfprobeInstalledAsync()
        {
            var path = ResolveFfprobePath();
            if (path == null)
            {
                _logger.LogInformation(
                    "No packaged ffprobe for runtimes {Rids} and none at {LegacyDirectory}",
                    string.Join(", ", _runtimeIdentifiers),
                    _legacyDirectory);
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                EnsureExecutable(path);
            }

            return (await ValidateFfprobeAsync(path)).Failure == null ? path : null;
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
