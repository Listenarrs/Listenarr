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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Common
{
    public class LegacyOutputPathMigrator : ILegacyOutputPathMigrator
    {
        private readonly IConfigurationService _configurationService;
        private readonly IRootFolderService _rootFolderService;
        private readonly ILogger<LegacyOutputPathMigrator> _logger;

        public LegacyOutputPathMigrator(IConfigurationService configurationService, IRootFolderService rootFolderService, ILogger<LegacyOutputPathMigrator> logger)
        {
            _configurationService = configurationService;
            _rootFolderService = rootFolderService;
            _logger = logger;
        }

        public async Task MigrateAsync()
        {
            try
            {
                var appSettings = await _configurationService.GetApplicationSettingsAsync();
                if (appSettings == null || string.IsNullOrWhiteSpace(appSettings.OutputPath))
                {
                    _logger.LogDebug("No legacy output path present; skipping migration");
                    return;
                }

                var existing = await _rootFolderService.GetAllAsync();
                if (existing != null && existing.Any())
                {
                    _logger.LogDebug("Root folders already exist; skipping legacy output path migration");
                    return;
                }

                var root = new RootFolder
                {
                    Name = "Default",
                    Path = appSettings.OutputPath!,
                    IsDefault = true
                };

                await _rootFolderService.CreateAsync(root);
                _logger.LogInformation("Migrated legacy ApplicationSettings.outputPath '{Path}' to RootFolder 'Default'", appSettings.OutputPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to migrate legacy ApplicationSettings.outputPath to RootFolder");
            }
        }
    }
}
