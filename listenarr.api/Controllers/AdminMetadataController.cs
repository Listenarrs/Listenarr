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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/admin")]
    [Tags("System")]
    public class AdminMetadataController(
        IAudiobookFileRepository audioFiles,
        Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery,
        IMetadataService metadataService) : ControllerBase
    {
        /// <summary>
        /// Re-extract audio metadata (duration, format, bitrate, etc.) for a single audiobook file.
        /// </summary>
        /// <param name="audiobookFileId">The database ID of the AudiobookFile to re-extract.</param>
        /// <returns>Confirmation of the re-extraction result.</returns>
        /// <response code="200">Re-extraction completed successfully.</response>
        /// <response code="400">Invalid CSRF token or the file has no path.</response>
        /// <response code="404">AudiobookFile not found.</response>
        [HttpPost("reextract-file/{audiobookFileId}")]
        public async Task<IActionResult> ReextractFile(int audiobookFileId)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return BadRequest(new { message = "Invalid or missing CSRF token", detail = ex.Message });
            }

            var file = await audioFiles.GetByIdAsync(audiobookFileId);
            if (file == null)
                return NotFound(new { message = "AudiobookFile not found" });

            var path = file.Path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return BadRequest(new { message = "AudiobookFile has no path" });

            AudioMetadata? meta = null;
            try
            {
                meta = await metadataService.ExtractFileMetadataAsync(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return StatusCode(500, new { message = "Metadata extraction failed", detail = ex.Message });
            }

            var fi = new System.IO.FileInfo(path);
            file.Size = fi.Exists ? fi.Length : file.Size;
            file.DurationSeconds = meta?.Duration.TotalSeconds ?? file.DurationSeconds;
            file.Format = string.IsNullOrEmpty(meta?.Format) ? file.Format : meta!.Format;
            file.Bitrate = (meta?.BitRate != 0) ? meta?.BitRate : file.Bitrate;
            file.SampleRate = meta?.SampleRate ?? file.SampleRate;
            file.Channels = meta?.Channels ?? file.Channels;

            await audioFiles.UpdateAsync(file);

            return Ok(new { message = "Re-extraction completed", audiobookFileId = audiobookFileId });
        }

        /// <summary>
        /// Trigger a batch metadata rescan for audiobook files that are missing duration, format, or sample rate.
        /// </summary>
        /// <param name="max">Maximum number of files to process in this batch (default 50).</param>
        /// <returns>Summary with the number of files examined and updated.</returns>
        [HttpPost("trigger-rescan")]
        public async Task<IActionResult> TriggerRescan([FromQuery] int max = 50)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return BadRequest(new { message = "Invalid or missing CSRF token", detail = ex.Message });
            }

            var candidates = await audioFiles.GetMissingMetadataAsync(max);

            var updated = 0;
            foreach (var f in candidates)
            {
                try
                {
                    var meta = await metadataService.ExtractFileMetadataAsync(f.Path ?? string.Empty);
                    if (meta != null)
                    {
                        var fi = new System.IO.FileInfo(f.Path ?? string.Empty);
                        f.Size = fi.Exists ? fi.Length : f.Size;
                        f.DurationSeconds = meta.Duration.TotalSeconds != 0 ? meta.Duration.TotalSeconds : f.DurationSeconds;
                        f.Format = !string.IsNullOrEmpty(meta.Format) ? meta.Format : f.Format;
                        f.Bitrate = meta.BitRate != 0 ? meta.BitRate : f.Bitrate;
                        f.SampleRate = meta.SampleRate != 0 ? meta.SampleRate : f.SampleRate;
                        f.Channels = meta.Channels != 0 ? meta.Channels : f.Channels;
                        await audioFiles.UpdateAsync(f);
                        updated++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // log and continue
                    HttpContext.RequestServices.GetRequiredService<ILogger<AdminMetadataController>>()
                        .LogWarning(ex, "Failed to re-extract for file id={Id} path={Path}", f.Id, f.Path);
                }
            }

            return Ok(new { message = "Triggered rescan", examined = candidates.Count, updated });
        }
    }
}
