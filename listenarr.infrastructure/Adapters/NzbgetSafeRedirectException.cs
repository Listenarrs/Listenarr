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
namespace Listenarr.Infrastructure.Adapters
{
    /// <summary>
    /// Thrown by <see cref="NzbgetSafeRedirectHandler"/> when a redirect is refused (cross-host,
    /// HTTPS->HTTP downgrade, missing Location, or loop). Carries a descriptive message that
    /// the adapter surfaces to the user instead of the generic "network error" fallback.
    /// </summary>
    public sealed class NzbgetSafeRedirectException : HttpRequestException
    {
        public NzbgetSafeRedirectException(string message) : base(message) { }
    }
}
