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
using Listenarr.Application.Integrations.Audiobookshelf.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Audiobookshelf
{
    [ApiController]
    [Route("api/v{version:apiVersion}/audiobookshelf")]
    [RequireAdminOrApiKey]
    [Tags("Audiobookshelf")]
    public class AudiobookshelfController : ControllerBase
    {
        private readonly IAudiobookshelfService _audiobookshelfService;

        public AudiobookshelfController(IAudiobookshelfService audiobookshelfService)
        {
            _audiobookshelfService = audiobookshelfService;
        }

        /// <summary>
        /// Test connectivity to an Audiobookshelf server. Blank fields fall back to the
        /// saved connection, so a saved configuration can be re-tested without re-entering
        /// the API token. Returns the server's libraries on success.
        /// </summary>
        [HttpPost("test")]
        public async Task<ActionResult<object>> TestConnection([FromBody] AudiobookshelfTestRequestDto? request, CancellationToken cancellationToken)
        {
            var result = await _audiobookshelfService.TestConnectionAsync(request?.Url, request?.ApiKey, cancellationToken);
            return Ok(new { success = result.Success, message = result.Message, libraries = result.Libraries });
        }

        /// <summary>
        /// List libraries from the configured Audiobookshelf server (for the settings library picker).
        /// </summary>
        [HttpGet("libraries")]
        public async Task<ActionResult<object>> GetLibraries(CancellationToken cancellationToken)
        {
            var result = await _audiobookshelfService.GetLibrariesAsync(cancellationToken);
            return Ok(new { success = result.Success, message = result.Message, libraries = result.Libraries });
        }

        /// <summary>
        /// Request an Audiobookshelf scan of the configured library (or all book libraries),
        /// so newly imported files show up without a manual scan in Audiobookshelf.
        /// </summary>
        [HttpPost("scan")]
        public async Task<ActionResult<object>> TriggerScan(CancellationToken cancellationToken)
        {
            var result = await _audiobookshelfService.TriggerScanAsync(cancellationToken);
            return Ok(new { success = result.Success, message = result.Message });
        }
    }

    public sealed class AudiobookshelfTestRequestDto
    {
        public string? Url { get; set; }
        public string? ApiKey { get; set; }
    }
}
