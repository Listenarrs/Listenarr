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
    public partial class LibraryController
    {
        /// <summary>
        /// Aggregate view of the background move queue (counts + in-flight +
        /// recent tails) for the maintenance banner.
        /// </summary>
        /// <param name="recentLimit">How many recent completed/failed jobs to include (clamped to [0, 200]).</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpGet("move/summary")]
        public async Task<IActionResult> GetMoveQueueSummary([FromQuery] int recentLimit = 25, CancellationToken ct = default)
        {
            return await _moveSummaryWorkflow.SummaryAsync(recentLimit, ct);
        }

        /// <summary>
        /// Preview the library-wide Organize sweep: bucket every audiobook
        /// into already_canonical / will_move / collision / invalid_target
        /// against the path its metadata computes to. Read-only.
        /// </summary>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpGet("organize/preview")]
        public async Task<IActionResult> GetOrganizePreview(CancellationToken ct)
        {
            return await _organizeSweepWorkflow.PreviewAsync(ct);
        }

        /// <summary>
        /// Queue a background move for each confirmed preview row. Every id is
        /// re-validated against live state before queuing; rows that no longer
        /// qualify are skipped with a reason instead of aborting the rest.
        /// </summary>
        /// <param name="request">Audiobook ids confirmed in the preview UI.</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpPost("organize/apply")]
        public async Task<IActionResult> ApplyOrganize([FromBody] OrganizeLibraryApplyRequest request, CancellationToken ct)
        {
            return await _organizeSweepWorkflow.ApplyAsync(request, ct);
        }

        /// <summary>
        /// Collapse a single "nested one level too deep" preview row into its
        /// canonical parent folder. Strongly guarded — only empty wrapper
        /// directories are ever collapsed.
        /// </summary>
        /// <param name="request">The audiobook id of the nested row.</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpPost("organize/flatten")]
        public async Task<IActionResult> FlattenOrganize([FromBody] OrganizeFlattenRequest request, CancellationToken ct)
        {
            return await _organizeSweepWorkflow.FlattenAsync(request, ct);
        }
    }
}
