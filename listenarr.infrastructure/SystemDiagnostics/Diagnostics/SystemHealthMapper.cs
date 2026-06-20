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

namespace Listenarr.Infrastructure.SystemDiagnostics.Diagnostics
{
    internal static class SystemHealthMapper
    {
        public static ServiceHealth BuildServiceHealth(
            string version,
            string uptime,
            DownloadClientHealth downloadClientHealth,
            ExternalApiHealth externalApiHealth)
        {
            var overallStatus = "healthy";
            if (downloadClientHealth.Status == "error" || externalApiHealth.Status == "error")
            {
                overallStatus = "error";
            }
            else if (downloadClientHealth.Status == "warning" || externalApiHealth.Status == "warning")
            {
                overallStatus = "warning";
            }

            return new ServiceHealth
            {
                Status = overallStatus,
                Version = version,
                Uptime = uptime,
                DownloadClients = downloadClientHealth,
                ExternalApis = externalApiHealth
            };
        }

        public static DownloadClientHealth BuildDownloadClientHealth(IEnumerable<DownloadClientConfiguration> clients)
        {
            var clientList = clients?.ToList() ?? new List<DownloadClientConfiguration>();
            var clientStatuses = new List<ClientStatus>();
            var connectedCount = 0;

            foreach (var client in clientList)
            {
                if (!client.IsEnabled)
                {
                    continue;
                }

                var status = "connected";
                connectedCount++;

                clientStatuses.Add(new ClientStatus
                {
                    Name = client.Name,
                    Status = status,
                    Type = client.Type
                });
            }

            var totalEnabled = clientList.Count(c => c.IsEnabled);
            var overallStatus = BuildChildStatus(connectedCount, totalEnabled);

            return new DownloadClientHealth
            {
                Status = overallStatus,
                Connected = connectedCount,
                Total = totalEnabled,
                Clients = clientStatuses
            };
        }

        public static ExternalApiHealth BuildExternalApiHealth(IEnumerable<ApiConfiguration> apis)
        {
            var apiList = apis?.ToList() ?? new List<ApiConfiguration>();
            var apiStatuses = new List<ApiStatus>();
            var connectedCount = 0;

            foreach (var api in apiList)
            {
                if (!api.IsEnabled)
                {
                    continue;
                }

                var status = "connected";
                connectedCount++;

                apiStatuses.Add(new ApiStatus
                {
                    Name = api.Name,
                    Status = status,
                    Enabled = api.IsEnabled
                });
            }

            var totalEnabled = apiList.Count(c => c.IsEnabled);
            var overallStatus = BuildChildStatus(connectedCount, totalEnabled);

            return new ExternalApiHealth
            {
                Status = overallStatus,
                Connected = connectedCount,
                Total = totalEnabled,
                Apis = apiStatuses
            };
        }

        public static DownloadClientHealth BuildDownloadClientHealthError()
        {
            return new DownloadClientHealth
            {
                Status = "error",
                Connected = 0,
                Total = 0,
                Clients = new List<ClientStatus>()
            };
        }

        public static ExternalApiHealth BuildExternalApiHealthError()
        {
            return new ExternalApiHealth
            {
                Status = "error",
                Connected = 0,
                Total = 0,
                Apis = new List<ApiStatus>()
            };
        }

        private static string BuildChildStatus(int connectedCount, int totalEnabled)
        {
            if (connectedCount == 0 && totalEnabled > 0)
            {
                return "error";
            }

            if (connectedCount < totalEnabled)
            {
                return "warning";
            }

            return "healthy";
        }
    }
}
