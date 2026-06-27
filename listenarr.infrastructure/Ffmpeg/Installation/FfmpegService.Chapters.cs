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
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    public partial class FfmpegService : IFfmpegService
    {
        public async Task<IReadOnlyList<FfprobeChapter>> RunFfprobeChaptersAsync(string filePath)
        {
            var sanitizedFilePath = LogRedaction.SanitizeFilePath(filePath);
            try
            {
                if (!File.Exists(_ffprobePath))
                {
                    _logger.LogWarning("ffprobe binary unavailable; treating {File} as chapter-less", sanitizedFilePath);
                    return Array.Empty<FfprobeChapter>();
                }

                if (!FileSystemSafety.TryValidateMutationTarget(_ffprobePath, [_baseDir], out var safeFfprobePath, out _))
                {
                    _logger.LogWarning("ffprobe binary outside configured root; treating {File} as chapter-less", sanitizedFilePath);
                    return Array.Empty<FfprobeChapter>();
                }

                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("ffprobe chapters target does not exist: {File}", sanitizedFilePath);
                    return Array.Empty<FfprobeChapter>();
                }

                var safeFilePath = Path.GetFullPath(filePath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = safeFfprobePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-v");
                startInfo.ArgumentList.Add("quiet");
                startInfo.ArgumentList.Add("-print_format");
                startInfo.ArgumentList.Add("json");
                startInfo.ArgumentList.Add("-show_chapters");
                startInfo.ArgumentList.Add(safeFilePath);

                var pr = await _processRunner.RunAsync(startInfo, 10000);

                if (pr.ExitCode > 0 || string.IsNullOrEmpty(pr.Stdout))
                {
                    _logger.LogInformation("ffprobe chapters returned no output for {File} (exit={Code})", sanitizedFilePath, pr.ExitCode);
                    return Array.Empty<FfprobeChapter>();
                }

                using var doc = JsonDocument.Parse(pr.Stdout);
                if (!doc.RootElement.TryGetProperty("chapters", out var chaptersEl)
                    || chaptersEl.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<FfprobeChapter>();
                }

                var result = new List<FfprobeChapter>();
                foreach (var ch in chaptersEl.EnumerateArray())
                {
                    var start = ParseTimeString(ch, "start_time");
                    var end   = ParseTimeString(ch, "end_time");
                    var title = string.Empty;

                    if (ch.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                        && tags.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                    {
                        title = titleEl.GetString() ?? string.Empty;
                    }

                    result.Add(new FfprobeChapter(start, end, title));
                }

                _logger.LogDebug("ffprobe found {Count} chapters in {File}", result.Count, sanitizedFilePath);
                return result;
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(ex, "ffprobe chapters failed for {File}; treating as chapter-less", sanitizedFilePath);
                return Array.Empty<FfprobeChapter>();
            }
        }

        private static double ParseTimeString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return d;
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
            }
            return 0.0;
        }
    }
}
