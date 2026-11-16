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
        public Task<AudioMetadata> RunFfprobeAsync(string filePath)
        {
            return RunFfprobeAsync(new MetadataFileSource(filePath, filePath));
        }

        public async Task<AudioMetadata> RunFfprobeAsync(
            MetadataFileSource fileSource)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileSource.ReadPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(fileSource.PublicPath);
            var sanitizedPublicPath = LogRedaction.SanitizeFilePath(
                fileSource.PublicPath);
            JsonElement ffprobeData;
            try
            {
                var ffprobePath = ResolveFfprobePath()
                    ?? throw new FfmpegException("ffprobe binary is unavailable.");

                if (!FileSystemSafety.TryValidateMutationTarget(ffprobePath, [_applicationBaseDirectory, _legacyDirectory], out var safeFfprobePath, out var ffprobeReason))
                {
                    throw new FfmpegException($"ffprobe binary is unavailable or outside configured root: {LogRedaction.SanitizeText(ffprobeReason)}");
                }

                if (!File.Exists(fileSource.ReadPath))
                {
                    throw new FfmpegException($"ffprobe target does not exist: {sanitizedPublicPath}");
                }

                if (!FileUtils.IsAudioFile(fileSource.PublicPath)
                    && !FileUtils.IsAmbiguousAudioContainer(fileSource.PublicPath))
                {
                    throw new FfmpegException($"ffprobe target is not a supported audio file: {sanitizedPublicPath}");
                }

                var safeReadPath = Path.GetFullPath(fileSource.ReadPath);
                _logger.LogInformation("Running ffprobe at {Path} against file {File}", safeFfprobePath, sanitizedPublicPath);

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
                startInfo.ArgumentList.Add(safeReadPath);

                var pr = await _processRunner.RunAsync(startInfo, 10000);
                _logger.LogInformation("ffprobe exit code {Code} for file {File}; stderr length={Len}", pr.ExitCode, sanitizedPublicPath, pr.Stderr?.Length ?? 0);

                if (pr.TimedOut || pr.ExitCode != 0)
                {
                    throw new FfmpegException($"ffprobe cannot read/process {sanitizedPublicPath}");
                }

                if (string.IsNullOrEmpty(pr.Stdout))
                {
                    throw new FfmpegException($"Failed to parse ffprobe JSON output for {sanitizedPublicPath}: Cannot retrieve output or retrieved empty output");
                }

                try
                {
                    ffprobeData = JsonSerializer.Deserialize<JsonElement>(pr.Stdout);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    throw new FfmpegException($"Failed to parse ffprobe JSON output for {sanitizedPublicPath}", ex);
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new FfmpegException($"ffprobe execution failed for {sanitizedPublicPath}", ex);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new FfmpegException($"Error running ffprobe for {sanitizedPublicPath}", ex);
            }

            var metadata = FfprobeMetadataMapper.Map(
                ffprobeData,
                fileSource.PublicPath);

            _logger.LogInformation("Extracted ffprobe metadata from file: {File}", sanitizedPublicPath);
            _logger.LogDebug("Parsed metadata: Duration={Duration} seconds, Format={Format}, Bitrate={Bitrate}, SampleRate={SampleRate}, Channels={Channels}", metadata.Duration.TotalSeconds, metadata.Format, metadata.BitRate, metadata.SampleRate, metadata.Channels);

            return metadata;
        }

        public Task<string> GetLicenseAsync()
        {
            var path = ResolveFfprobePath();
            if (path == null)
            {
                return Task.FromResult(string.Empty);
            }

            return Task.FromResult(path == ResolvePackagedFfprobePath() ? PackagedLicenseNotice : LegacyLicenseNotice);
        }
    }
}
