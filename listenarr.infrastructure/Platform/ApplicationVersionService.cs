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

using System.Diagnostics;
using System.Reflection;
using Listenarr.Application.Interfaces;

namespace Listenarr.Infrastructure.Platform
{
    public sealed class ApplicationVersionService : IApplicationVersionService
    {
        private readonly IHostEnvironment _hostEnvironment;

        public ApplicationVersionService(IHostEnvironment hostEnvironment)
        {
            _hostEnvironment = hostEnvironment;
        }

        public string Resolve()
        {
            var assembly = ResolveAssembly();
            return GetInformationalVersion(assembly)
                ?? GetProductVersion(assembly)
                ?? assembly?.GetName().Version?.ToString()
                ?? "unknown";
        }

        private Assembly? ResolveAssembly()
        {
            var applicationName = _hostEnvironment.ApplicationName;
            if (!string.IsNullOrWhiteSpace(applicationName))
            {
                var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => string.Equals(
                        assembly.GetName().Name,
                        applicationName,
                        StringComparison.OrdinalIgnoreCase));

                if (loadedAssembly != null)
                {
                    return loadedAssembly;
                }

                try
                {
                    return Assembly.Load(new AssemblyName(applicationName));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Fall through to entry assembly resolution.
                }
            }

            return Assembly.GetEntryAssembly()
                ?? Assembly.GetExecutingAssembly();
        }

        private static string? GetInformationalVersion(Assembly? assembly)
        {
            var version = assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            return NormalizeVersion(version);
        }

        private static string? GetProductVersion(Assembly? assembly)
        {
            if (string.IsNullOrWhiteSpace(assembly?.Location))
            {
                return null;
            }

            try
            {
                var fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
                return NormalizeVersion(fileVersionInfo.ProductVersion)
                    ?? NormalizeVersion(fileVersionInfo.FileVersion);
            }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or PathTooLongException)
            {
                return null;
            }
        }

        private static string? NormalizeVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var trimmedVersion = version.Trim();
            var metadataIndex = trimmedVersion.IndexOf('+');
            return metadataIndex > 0
                ? trimmedVersion[..metadataIndex]
                : trimmedVersion;
        }
    }
}
