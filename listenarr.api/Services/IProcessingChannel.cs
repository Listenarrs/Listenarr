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
namespace Listenarr.Api.Services
{
    /// <summary>
    /// Abstraction over an in-memory processing channel so consumers can be decoupled from the concrete channel implementation.
    /// </summary>
    public interface IProcessingChannel
    {
        ValueTask EnqueueJobAsync(string jobId, CancellationToken ct = default);
        IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct = default);
        bool TryWrite(string jobId);
    }
}
