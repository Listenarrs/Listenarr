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

namespace Listenarr.Infrastructure.Factories
{
    public class DownloadClientAdapterFactory : IDownloadClientAdapterFactory
    {
        private readonly Dictionary<string, IDownloadClientAdapter> _byId;
        private readonly Dictionary<string, IDownloadClientAdapter> _byType;

        public DownloadClientAdapterFactory(IEnumerable<IDownloadClientAdapter> adapters)
        {
            var list = adapters?.ToList() ?? new List<IDownloadClientAdapter>();
            _byId = list
                .Where(a => !string.IsNullOrWhiteSpace(a.ClientId))
                .GroupBy(a => a.ClientId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            _byType = list
                .Where(a => !string.IsNullOrWhiteSpace(a.ClientType))
                .GroupBy(a => a.ClientType!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        public IDownloadClientAdapter GetByIdOrType(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("Adapter key not provided.");
            }

            if (_byId.TryGetValue(id, out var byId)) return byId;
            if (_byType.TryGetValue(id, out var byType)) return byType;
            throw new InvalidOperationException($"No adapter registered for key '{id}'.");
        }
    }
}
