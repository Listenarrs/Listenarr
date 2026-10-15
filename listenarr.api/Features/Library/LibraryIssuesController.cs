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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    [ApiController]
    [Route("api/v{version:apiVersion}/library/issues")]
    [Tags("Library")]
    public class LibraryIssuesController : ControllerBase
    {
        private readonly ILibraryIssueService _libraryIssueService;

        public LibraryIssuesController(ILibraryIssueService libraryIssueService)
        {
            _libraryIssueService = libraryIssueService;
        }

        /// <summary>
        /// List the books whose Audible identity is incomplete (no title, no ASIN, a malformed ASIN,
        /// or an ASIN without its region), to repair them.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetIssues(CancellationToken ct)
        {
            return Ok(await _libraryIssueService.GetIssuesAsync(ct));
        }
    }
}
