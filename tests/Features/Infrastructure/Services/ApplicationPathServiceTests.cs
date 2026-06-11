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

using Listenarr.Infrastructure.Services;

namespace Listenarr.Tests.Features.Infrastructure.Services
{
    [Trait("Area", "Infrastructure")]
    [Trait("Name", "ApplicationPathServiceTests")]
    [Trait("Category", "ApplicationPathService")]
    public class ApplicationPathServiceTests
    {
        [Fact]
        [Trait("Method", "Constructor")]
        [Trait("Scenario", "ExposesDiscordBotRootPathUnderToolsRoot")]
        public void Constructor_ExposesDiscordBotRootPathUnderToolsRoot()
        {
            var contentRootPath = Path.Join(Path.GetTempPath(), "listenarr-path-service-tests", Guid.NewGuid().ToString("N"));
            var service = new ApplicationPathService(contentRootPath);

            Assert.Equal(
                Path.GetFullPath(Path.Join(contentRootPath, "tools", "discord-bot")),
                service.DiscordBotRootPath);
        }
    }
}
