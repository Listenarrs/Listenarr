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

namespace Listenarr.Application.Search.DomainRotation
{
    /// <summary>
    /// Tests whether a host is reachable over HTTP/HTTPS.
    /// Abstracted to allow mocking in unit tests.
    /// </summary>
    public interface IDomainReachabilityChecker
    {
        /// <summary>
        /// Returns true if the host responds to a lightweight probe request.
        /// DNS failure, connection timeout, or network error → false.
        /// Any HTTP response (including 403 Cloudflare-blocked) → true.
        /// </summary>
        Task<bool> IsReachableAsync(string scheme, string host, CancellationToken ct);
    }
}
