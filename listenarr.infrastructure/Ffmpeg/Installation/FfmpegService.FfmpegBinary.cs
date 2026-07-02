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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    // ffmpeg-binary (as opposed to ffprobe) support for the audio-verification
    // sample extractor (ADR-0001). Split into a partial to keep the main
    // installation file under the architecture size cap.
    public partial class FfmpegService
    {
        /// <summary>
        /// Return the ffmpeg path if it exists in the bundled directory. Does NOT
        /// attempt to download or install.
        /// </summary>
        public Task<string?> GetFfmpegPathAsync()
        {
            if (File.Exists(_ffmpegPath))
            {
                return Task.FromResult<string?>(_ffmpegPath);
            }

            _logger.LogInformation("No bundled ffmpeg found at {Path}", _ffmpegPath);
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Ensure the ffmpeg binary is available in the bundled directory. The static archive
        /// downloaded for ffprobe contains ffmpeg as well, so this first tries to promote a
        /// binary left behind in the extracted tree by a previous install, then falls back to
        /// the full download flow (which extracts both binaries).
        /// </summary>
        public async Task<string?> EnsureFfmpegInstalledAsync()
        {
            if (File.Exists(_ffmpegPath))
            {
                return _ffmpegPath;
            }

            if (await TryPromoteExtractedBinaryAsync(_ffmpegName, _ffmpegPath))
            {
                return _ffmpegPath;
            }

            if (!_autoInstall)
            {
                _logger.LogInformation("Auto-install of ffmpeg is disabled via LISTENARR_AUTO_INSTALL_FFPROBE");
                return null;
            }

            if (await GetFfprobePathAsync() == null)
            {
                await EnsureFfprobeInstalledAsync();
                if (File.Exists(_ffmpegPath) || await TryPromoteExtractedBinaryAsync(_ffmpegName, _ffmpegPath))
                {
                    return File.Exists(_ffmpegPath) ? _ffmpegPath : null;
                }
            }

            _logger.LogWarning(
                "ffmpeg binary unavailable: ffprobe is installed but the extracted archive no longer contains ffmpeg. Delete {Path} and restart to re-download the archive.",
                _ffprobePath);
            return null;
        }

        /// <summary>
        /// Locate a binary by name in the extracted archive tree under the base directory and
        /// move it to <paramref name="destPath"/>, setting the executable bit on non-Windows.
        /// Returns true when the binary exists at the destination afterwards.
        /// </summary>
        private async Task<bool> TryPromoteExtractedBinaryAsync(string binaryName, string destPath)
        {
            if (File.Exists(destPath)) return true;

            try
            {
                if (!Directory.Exists(_baseDir)) return false;

                var candidates = Directory.GetFiles(_baseDir, binaryName, SearchOption.AllDirectories)
                    .Where(p => !string.Equals(Path.GetFullPath(p), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Prefer candidates in a 'bin' directory (common for ffmpeg archives),
                // then the shortest path.
                var chosen = candidates
                    .OrderByDescending(p => p.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenBy(p => p.Length)
                    .FirstOrDefault();
                if (string.IsNullOrEmpty(chosen))
                {
                    _logger.LogInformation("No {Binary} binary found in extracted files under {BaseDir}", binaryName, _baseDir);
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? _baseDir);
                try
                {
                    File.Move(chosen, destPath);
                }
                catch (Exception mvEx) when (mvEx is not OperationCanceledException && mvEx is not OutOfMemoryException && mvEx is not StackOverflowException)
                {
                    File.Copy(chosen, destPath, overwrite: true);
                    _logger.LogDebug(mvEx, "Move of {Binary} failed; copied instead", binaryName);
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x \"{destPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        await _processRunner.RunAsync(psi, 10000);
                    }
                    catch (Exception chEx) when (chEx is not OperationCanceledException && chEx is not OutOfMemoryException && chEx is not StackOverflowException)
                    {
                        _logger.LogDebug(chEx, "chmod +x failed for {Path}", destPath);
                    }
                }

                _logger.LogInformation("Promoted {Binary} from {Src} to {Dest}", binaryName, chosen, destPath);
                return File.Exists(destPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to promote {Binary} from the extracted archive", binaryName);
                return false;
            }
        }
    }
}
