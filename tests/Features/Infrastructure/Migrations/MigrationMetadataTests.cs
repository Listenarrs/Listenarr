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
using System.Reflection;
using Listenarr.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Migrations
{
    public class MigrationMetadataTests
    {
        [Fact]
        public void AddImportBlacklistExtensionsMigration_IsDiscoverableByEf()
        {
            var attribute = typeof(AddImportBlacklistExtensionsToApplicationSettings)
                .GetCustomAttribute<MigrationAttribute>();

            Assert.NotNull(attribute);
            Assert.Equal("20260317123000_AddImportBlacklistExtensionsToApplicationSettings", attribute!.Id);
        }
    }
}
