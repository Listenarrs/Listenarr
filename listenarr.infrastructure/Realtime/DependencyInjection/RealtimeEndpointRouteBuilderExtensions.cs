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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Listenarr.Infrastructure.Realtime.DependencyInjection
{
    public static class RealtimeEndpointRouteBuilderExtensions
    {
        public static IEndpointRouteBuilder MapListenarrRealtimeHubs(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
        {
            if (environment.IsDevelopment())
            {
                endpoints.MapHub<DownloadHub>("/hubs/downloads").RequireCors("DevOnly");
                endpoints.MapHub<LogHub>("/hubs/logs").RequireCors("DevOnly");
                endpoints.MapHub<SettingsHub>("/hubs/settings").RequireCors("DevOnly");
                return endpoints;
            }

            endpoints.MapHub<DownloadHub>("/hubs/downloads");
            endpoints.MapHub<LogHub>("/hubs/logs");
            endpoints.MapHub<SettingsHub>("/hubs/settings");
            return endpoints;
        }
    }
}
