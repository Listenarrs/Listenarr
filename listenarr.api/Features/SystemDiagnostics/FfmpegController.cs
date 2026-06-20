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
using Listenarr.Api.Attributes;
using Listenarr.Api.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.SystemDiagnostics
{
    [ApiController]
    [LocalOrAdmin]
    [Route("api/v{version:apiVersion}/ffmpeg")]
    [Tags("System")]
    public class FfmpegController : ControllerBase
    {
        private readonly IFfmpegService _ffmpegService;
        private readonly ILogger<FfmpegController> _logger;
        private readonly IProcessRunner? _processRunner;
        private readonly IFileSystem _fileSystem;

        public FfmpegController(
            IFfmpegService ffmpegService,
            ILogger<FfmpegController> logger,
            IFileSystem fileSystem,
            IProcessRunner? processRunner = null)
        {
            _ffmpegService = ffmpegService;
            _logger = logger;
            _fileSystem = fileSystem;
            _processRunner = processRunner;
        }

        /// <summary>
        /// Get the path to the bundled ffprobe binary and the associated license notice.
        /// </summary>
        /// <remarks>Restricted to local or admin callers.</remarks>
        [HttpGet("info")]
        public async Task<IActionResult> GetInfo()
        {
            return Ok(new
            {
                ffprobePath = await _ffmpegService.GetFfprobePathAsync(),
                licenseNotice = await _ffmpegService.GetLicenseAsync()
            });
        }

        /// <summary>
        /// Run ffprobe against a local audio file and return the raw JSON output.
        /// </summary>
        /// <param name="req">Request body containing the absolute path to the file to scan.</param>
        /// <remarks>Restricted to local or admin callers. Only absolute, local file paths are accepted.</remarks>
        /// <response code="200">ffprobe output including parsed JSON, exit code, stdout, and stderr.</response>
        /// <response code="400">File path missing, relative, or non-local.</response>
        /// <response code="404">File not found at the specified path.</response>
        [HttpPost("scan")]
        public async Task<IActionResult> RunFfprobe([FromBody] FfprobeScanRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.FilePath)) return BadRequest(new { message = "FilePath is required" });

            var requestedPath = req.FilePath!;
            if (Uri.TryCreate(requestedPath, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                return BadRequest(new { message = "Only local file paths are allowed" });
            }

            if (!Path.IsPathRooted(requestedPath))
            {
                return BadRequest(new { message = "FilePath must be an absolute path" });
            }

            string filePath;
            try
            {
                filePath = Path.GetFullPath(requestedPath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return BadRequest(new { message = "FilePath is invalid" });
            }

            if (!_fileSystem.FileExists(filePath))
            {
                return NotFound(new { message = "File not found" });
            }

            string? ffprobePath = await _ffmpegService.GetFfprobePathAsync();
            if (ffprobePath == null)
            {
                _logger.LogWarning("IProcessRunner is not available; cannot run ffprobe for {File}", LogRedaction.SanitizeFilePath(filePath));
                return StatusCode(500, new { message = "IProcessRunner service is not available to run external processes" });
            }

            try
            {
                object result = await _ffmpegService.RunFfprobeAsync(filePath);
                return Ok(new { ffprobePath, result });
            }
            catch (FfmpegException ex)
            {
                _logger.LogWarning(ex, "ffprobe execution failed for {File}", LogRedaction.SanitizeFilePath(filePath));
                return StatusCode(500, new { message = "ffprobe execution failed", error = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error running ffprobe for {File}", LogRedaction.SanitizeFilePath(filePath));
                return StatusCode(500, new { message = "Error running ffprobe", error = ex.Message });
            }
        }
    }
}
