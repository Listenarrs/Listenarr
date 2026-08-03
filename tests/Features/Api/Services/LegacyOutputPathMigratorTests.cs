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
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services
{
    public class LegacyOutputPathMigratorTests
    {
        private string booksPath = FileUtils.GetAbsolutePath("books");
        private string otherPath = FileUtils.GetAbsolutePath("other");

        [Fact]
        public async Task Migrate_CreatesRoot_WhenNoExistingAndOutputPathPresent()
        {
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = booksPath });

            var mockRootService = new Mock<IRootFolderService>();
            mockRootService.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<RootFolder>());
            mockRootService.Setup(r => r.CreateAsync(It.IsAny<RootFolder>())).ReturnsAsync((RootFolder r) => r);

            var migrator = new LegacyOutputPathMigrator(mockConfig.Object, mockRootService.Object, new NullLogger<LegacyOutputPathMigrator>());

            await migrator.MigrateAsync();

            mockRootService.Verify(r => r.CreateAsync(It.Is<RootFolder>(rf => rf.Name == "Default" && rf.Path == booksPath && rf.IsDefault)), Times.Once);
        }

        [Fact]
        public async Task Migrate_DoesNotCreate_WhenRootsExist()
        {
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = booksPath });

            var mockRootService = new Mock<IRootFolderService>();
            mockRootService.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<RootFolder> { new RootFolder { Name = "X", Path = otherPath } });

            var migrator = new LegacyOutputPathMigrator(mockConfig.Object, mockRootService.Object, new NullLogger<LegacyOutputPathMigrator>());

            await migrator.MigrateAsync();

            mockRootService.Verify(r => r.CreateAsync(It.IsAny<RootFolder>()), Times.Never);
        }

        [WindowsFact]
        public async Task Migrate_DoesNotCreate_WhenPersistedUnixRootWouldAliasCurrentWindowsDrive()
        {
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = "/" });

            var mockRootService = new Mock<IRootFolderService>();
            mockRootService.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<RootFolder>());

            var migrator = new LegacyOutputPathMigrator(
                mockConfig.Object,
                mockRootService.Object,
                new NullLogger<LegacyOutputPathMigrator>());

            await migrator.MigrateAsync();

            mockRootService.Verify(
                r => r.CreateAsync(It.IsAny<RootFolder>()),
                Times.Never);
        }

        [Fact]
        public async Task Migrate_DoesNotCreate_WhenOutputPathEmpty()
        {
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = "" });

            var mockRootService = new Mock<IRootFolderService>();
            mockRootService.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<RootFolder>());

            var migrator = new LegacyOutputPathMigrator(mockConfig.Object, mockRootService.Object, new NullLogger<LegacyOutputPathMigrator>());

            await migrator.MigrateAsync();

            mockRootService.Verify(r => r.CreateAsync(It.IsAny<RootFolder>()), Times.Never);
        }
    }
}
