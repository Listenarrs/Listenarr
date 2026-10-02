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

using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;

namespace Listenarr.Api.Startup;

public static class ListenarrPlatformRegistration
{
    public static IServiceCollection AddListenarrReverseProxyHeaders(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost |
                ForwardedHeaders.XForwardedPrefix;

            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("fc00::"), 7));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("fe80::"), 10));
        });

        return services;
    }

    public static IServiceCollection AddListenarrRealtime(this IServiceCollection services)
    {
        services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        return services;
    }

    public static IServiceCollection AddListenarrDevelopmentCors(this IServiceCollection services, IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            return services;
        }

        services.AddCors(options =>
        {
            options.AddPolicy("DevOnly",
                policy =>
                {
                    policy.WithOrigins(
                            "http://localhost:5173",
                            "https://localhost:5173",
                            "http://127.0.0.1:5173",
                            "https://127.0.0.1:5173")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
        });

        return services;
    }
}
