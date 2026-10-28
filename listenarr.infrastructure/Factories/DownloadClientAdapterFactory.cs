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
        private readonly Dictionary<string, List<IDownloadClientAdapter>> _byType;
        private readonly Dictionary<DownloadProtocol, List<IDownloadClientAdapter>> _byProtocol;

        public DownloadClientAdapterFactory(IEnumerable<IDownloadClientAdapter> adapters)
        {
            adapters = adapters?.ToList() ?? [];
            _byType = adapters
                .Where(a => !string.IsNullOrWhiteSpace(a.ClientType))
                .GroupBy(a => a.ClientType!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            _byProtocol = adapters
                .SelectMany(a => a.Protocols.Select(p => new { Protocol = p, Adapter = a }))
                .GroupBy(x => x.Protocol)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Adapter).ToList()
                );

            // Ensure one client type is always one adapter
            var duplicatedAdapters = _byType
                .Where(pair => pair.Value.Count > 1)
                .Select(pair => pair.Key);
            if (duplicatedAdapters.Count() > 0)
            {
                var duplicatedAdaptersString = string.Join(", ", duplicatedAdapters);
                throw new ArgumentException($"Multiple adapters found for the following client types: {duplicatedAdaptersString}. Each type must have only one adapter.");
            }
        }

        public IDownloadClientAdapter GetByType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new InvalidOperationException("Adapter key not provided.");
            }

            if (!_byType.TryGetValue(type, out var adapters))
            {
                throw new InvalidOperationException($"No adapter of type '{type}' registered.");
            }

            if (adapters.Count > 1)
            {
                throw new InvalidOperationException($"Multiple adapters of type '{type}' registered: Each client type can only have one adapter.");
            }

            return adapters.First();
        }

        public List<IDownloadClientAdapter> GetByProtocol(DownloadProtocol protocol)
        {
            if (!_byProtocol.TryGetValue(protocol, out var adapters))
            {
                throw new InvalidOperationException($"No adapter implementing '{protocol}' registered.");
            }

            return adapters;
        }

        public List<string> GetClientTypeSupportingProtocol(DownloadProtocol protocol)
        {
            return [.. GetByProtocol(protocol).Select(a => a.ClientType)];
        }
    }
}
