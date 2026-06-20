/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    public partial class FfmpegService : IFfmpegService
    {
        public async Task<AudioMetadata> RunFfprobeAsync(string filePath)
        {
            var sanitizedFilePath = LogRedaction.SanitizeFilePath(filePath);
            JsonElement ffprobeData;
            try
            {
                if (!File.Exists(_ffprobePath))
                {
                    throw new FfmpegException("ffprobe binary is unavailable.");
                }

                if (!FileSystemSafety.TryValidateMutationTarget(_ffprobePath, [_baseDir], out var safeFfprobePath, out var ffprobeReason))
                {
                    throw new FfmpegException($"ffprobe binary is unavailable or outside configured root: {LogRedaction.SanitizeText(ffprobeReason)}");
                }

                if (!File.Exists(filePath))
                {
                    throw new FfmpegException($"ffprobe target does not exist: {sanitizedFilePath}");
                }

                if (!FileUtils.IsAudioFile(filePath))
                {
                    throw new FfmpegException($"ffprobe target is not a supported audio file: {sanitizedFilePath}");
                }

                var safeFilePath = Path.GetFullPath(filePath);
                _logger.LogInformation("Running bundled ffprobe at {Path} against file {File}", safeFfprobePath, LogRedaction.SanitizeFilePath(safeFilePath));

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
                startInfo.ArgumentList.Add("-show_format");
                startInfo.ArgumentList.Add("-show_streams");
                startInfo.ArgumentList.Add(safeFilePath);

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

            var metadata = FfprobeMetadataMapper.Map(ffprobeData, filePath);

            _logger.LogInformation("Extracted ffprobe metadata from file: {File}", LogRedaction.SanitizeText(filePath));
            _logger.LogDebug("Parsed metadata: Duration={Duration} seconds, Format={Format}, Bitrate={Bitrate}, SampleRate={SampleRate}, Channels={Channels}", metadata.Duration.TotalSeconds, metadata.Format, metadata.BitRate, metadata.SampleRate, metadata.Channels);

            return metadata;
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
