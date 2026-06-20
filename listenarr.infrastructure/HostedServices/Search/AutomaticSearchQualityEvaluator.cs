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

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    internal sealed class AutomaticSearchQualityEvaluator(ILogger logger)
    {
        public async Task<(bool cutoffMet, string? bestExistingQuality)> GetExistingQualityAsync(
            Audiobook audiobook,
            IDownloadRepository downloadRepository,
            IAudiobookFileRepository fileRepository,
            CancellationToken ct = default)
        {
            var cutoffMet = await IsQualityCutoffMetAsync(audiobook, downloadRepository, fileRepository, ct);
            string? bestQuality = null;

            var allDownloads = await downloadRepository.GetByAudiobookIdAsync(audiobook.Id, ct);
            var existingDownloads = allDownloads.Where(d => d.Status == DownloadStatus.Completed).ToList();

            foreach (var dl in existingDownloads.Where(dl => dl.Metadata != null))
            {
                if (dl.Metadata!.TryGetValue("Quality", out var qobj) && qobj != null)
                {
                    var q = qobj.ToString();
                    if (!string.IsNullOrEmpty(q))
                    {
                        if (bestQuality == null) bestQuality = q;
                        else if (IsQualityBetter(q, bestQuality, audiobook.QualityProfile)) bestQuality = q;
                    }
                }
            }

            var existingFiles = await fileRepository.GetByAudiobookIdAsync(audiobook.Id, ct);

            foreach (var fq in existingFiles.Select(DetermineFileQuality).Where(fq => !string.IsNullOrEmpty(fq)))
            {
                if (bestQuality == null) bestQuality = fq;
                else if (IsQualityBetter(fq, bestQuality, audiobook.QualityProfile)) bestQuality = fq;
            }

            return (cutoffMet, bestQuality);
        }

        public bool IsQualityBetter(string? candidateQuality, string? existingQuality, QualityProfile? profile)
        {
            if (string.IsNullOrEmpty(candidateQuality)) return false;
            if (string.IsNullOrEmpty(existingQuality)) return true;
            if (profile == null) return false;

            var cand = profile.Qualities.FirstOrDefault(q => q.Quality == candidateQuality);
            var exist = profile.Qualities.FirstOrDefault(q => q.Quality == existingQuality);

            if (cand == null) return false;
            if (exist == null) return true;

            return cand.Priority > exist.Priority;
        }

        private async Task<bool> IsQualityCutoffMetAsync(
            Audiobook audiobook,
            IDownloadRepository downloadRepository,
            IAudiobookFileRepository fileRepository,
            CancellationToken ct = default)
        {
            if (audiobook.QualityProfile == null)
                return false;

            var allDownloads = await downloadRepository.GetByAudiobookIdAsync(audiobook.Id, ct);
            var existingDownloads = allDownloads.Where(d =>
                d.Status == DownloadStatus.Completed ||
                d.Status == DownloadStatus.Downloading ||
                d.Status == DownloadStatus.ImportPending).ToList();

            var existingFiles = await fileRepository.GetByAudiobookIdAsync(audiobook.Id, ct);

            if (!existingDownloads.Any() && !existingFiles.Any())
                return false;

            var cutoffQuality = audiobook.QualityProfile.Qualities
                .FirstOrDefault(q => q.Quality == audiobook.QualityProfile.CutoffQuality);

            if (cutoffQuality == null)
                return false;

            foreach (var download in existingDownloads)
            {
                if (download.Status == DownloadStatus.Completed && !string.IsNullOrEmpty(download.Metadata?.GetValueOrDefault("Quality")?.ToString()))
                {
                    var downloadQuality = download.Metadata["Quality"].ToString();
                    var downloadQualityDefinition = audiobook.QualityProfile.Qualities
                        .FirstOrDefault(q => q.Quality == downloadQuality);

                    if (downloadQualityDefinition != null && downloadQualityDefinition.Priority >= cutoffQuality.Priority)
                    {
                        logger.LogDebug("Quality cutoff met for audiobook '{Title}' by completed download (Quality: {Quality})",
                            audiobook.Title, downloadQuality);
                        return true;
                    }
                }
                else if (download.Status == DownloadStatus.Downloading || download.Status == DownloadStatus.ImportPending)
                {
                    logger.LogDebug("Quality cutoff assumed met for audiobook '{Title}' due to active download", audiobook.Title);
                    return true;
                }
            }

            foreach (var file in existingFiles)
            {
                var fileQuality = DetermineFileQuality(file);
                if (!string.IsNullOrEmpty(fileQuality))
                {
                    var fileQualityDefinition = audiobook.QualityProfile.Qualities
                        .FirstOrDefault(q => q.Quality == fileQuality);

                    if (fileQualityDefinition != null && fileQualityDefinition.Priority >= cutoffQuality.Priority)
                    {
                        logger.LogDebug("Quality cutoff met for audiobook '{Title}' by existing file (Quality: {Quality}, File: {FileName})",
                            audiobook.Title, fileQuality, Path.GetFileName(file.Path));
                        return true;
                    }
                }
            }

            return false;
        }

        private static string? DetermineFileQuality(AudiobookFile file)
        {
            if (!string.IsNullOrEmpty(file.Container))
            {
                var container = file.Container.ToLower();
                if (container.Contains("flac")) return "FLAC";
                if (container.Contains("m4b") || container.Contains("m4a")) return "M4B";
            }

            if (!string.IsNullOrEmpty(file.Format))
            {
                var format = file.Format.ToLower();
                if (format.Contains("flac")) return "FLAC";
                if (format.Contains("m4b") || format.Contains("m4a")) return "M4B";
                if (format.Contains("aac")) return "M4B";
            }

            if (file.Bitrate.HasValue)
            {
                var bitrate = file.Bitrate.Value;
                var kbps = bitrate / 1000;

                if (kbps >= 320) return "MP3 320kbps";
                if (kbps >= 256) return "MP3 256kbps";
                if (kbps >= 192) return "MP3 192kbps";
                if (kbps >= 128) return "MP3 128kbps";
                if (kbps >= 64) return "MP3 64kbps";

                return "MP3 64kbps";
            }

            if (!string.IsNullOrEmpty(file.Codec))
            {
                var codec = file.Codec.ToLower();
                if (codec.Contains("flac")) return "FLAC";
                if (codec.Contains("aac")) return "M4B";
                if (codec.Contains("mp3")) return "MP3 128kbps";
                if (codec.Contains("opus")) return "M4B";
            }

            if (!string.IsNullOrEmpty(file.Path))
            {
                var extension = Path.GetExtension(file.Path).ToLower();
                switch (extension)
                {
                    case ".flac":
                        return "FLAC";
                    case ".m4b":
                    case ".m4a":
                        return "M4B";
                    case ".mp3":
                        return "MP3 128kbps";
                    case ".aac":
                    case ".opus":
                        return "M4B";
                }
            }

            return null;
        }
    }
}
