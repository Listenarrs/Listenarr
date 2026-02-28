using System.IO;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System;

public class FfprobeScanRequest { public string? FilePath { get; set; } }

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/ffmpeg")]
    public class FfmpegController : ControllerBase
    {
        private readonly IFfmpegService _ffmpegService;
        private readonly ILogger<FfmpegController> _logger;
        private readonly IProcessRunner? _processRunner;

        public FfmpegController(IFfmpegService ffmpegService, ILogger<FfmpegController> logger, IProcessRunner? processRunner = null)
        {
            _ffmpegService = ffmpegService;
            _logger = logger;
            _processRunner = processRunner;
        }

        [HttpGet("info")]
        public async Task<IActionResult> GetInfo()
        {
            var gate = SensitiveEndpointAccessGuard.RequireLocalOrAdmin(HttpContext, _logger, "ffmpeg/info");
            if (gate != null) return gate;

            var path = await _ffmpegService.GetFfprobePathAsync(false);
            var baseDir = Path.Combine(Directory.GetCurrentDirectory(), "config", "ffmpeg");
            var licensePath = Path.Combine(baseDir, "LICENSE_NOTICE.txt");
            string license = string.Empty;
            if (System.IO.File.Exists(licensePath))
            {
                license = await System.IO.File.ReadAllTextAsync(licensePath);
            }

            return Ok(new { ffprobePath = path, licenseNotice = license });
        }

        [HttpPost("scan")]
        public async Task<IActionResult> RunFfprobe([FromBody] FfprobeScanRequest req)
        {
            var gate = SensitiveEndpointAccessGuard.RequireLocalOrAdmin(HttpContext, _logger, "ffmpeg/scan");
            if (gate != null) return gate;

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

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { message = "File not found" });
            }

            var ffprobeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
            var ffprobePath = Path.Combine(Directory.GetCurrentDirectory(), "config", "ffmpeg", ffprobeName);

            try
            {
                _logger.LogInformation("Running bundled ffprobe at {Path} against file {File}", ffprobePath, filePath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
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

                if (_processRunner != null)
                {
                    var pr = await _processRunner.RunAsync(startInfo, 10000);
                    _logger.LogInformation("ffprobe exit code {Code} for file {File}; stderr length={Len}", pr.ExitCode, LogRedaction.SanitizeFilePath(filePath), pr.Stderr?.Length ?? 0);

                    object? parsed = null;
                    if (!string.IsNullOrEmpty(pr.Stdout))
                    {
                        try { parsed = JsonSerializer.Deserialize<JsonElement>(pr.Stdout); }
                        catch (Exception jex) when (jex is not OperationCanceledException && jex is not OutOfMemoryException && jex is not StackOverflowException) { _logger.LogDebug(jex, "Failed to parse ffprobe JSON output for {File}", LogRedaction.SanitizeFilePath(filePath)); }
                    }

                    return Ok(new { ffprobePath, exitCode = pr.ExitCode, stdout = pr.Stdout, stderr = pr.Stderr, parsed });
                }
                else
                {
                    _logger.LogWarning("IProcessRunner is not available; cannot run ffprobe for {File}", LogRedaction.SanitizeFilePath(filePath));
                    return StatusCode(500, new { message = "IProcessRunner service is not available to run external processes" });
                }
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                _logger.LogWarning(wex, "ffprobe execution failed for {File}", LogRedaction.SanitizeFilePath(filePath));
                return StatusCode(500, new { message = "ffprobe execution failed", error = wex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error running ffprobe for {File}", LogRedaction.SanitizeFilePath(filePath));
                return StatusCode(500, new { message = "Error running ffprobe", error = ex.Message });
            }
        }
    }
}

