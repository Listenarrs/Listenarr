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
using Xunit;
using Moq;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Common;

namespace Listenarr.Tests.Features.Api.Services
{
    public class DownloadNaming_PatternCollapseTests
    {
        [Fact]
        public void ApplyNamingPattern_CollapsesAdjacentDuplicateComponents()
        {
            // Test the FileNamingService.ApplyNamingPattern method directly
            var loggerMock = new Mock<ILogger<FileNamingService>>();
            var configMock = new Mock<IConfigurationService>();
            var fileNamingService = new FileNamingService(configMock.Object, loggerMock.Object);

            // Pattern that produces duplicate components
            var pattern = "{Author}/{Series}/{Title} ({Year})";
            var variables = new Dictionary<string, object>
            {
                { "Author", "Jane Austen" },
                { "Series", null },
                { "Title", "Pride and Prejudice" },
                { "Year", "2015" }
            };

            var result = fileNamingService.ApplyNamingPattern(pattern, variables);

            // Should collapse adjacent duplicates: "Jane Austen/Pride and Prejudice/Pride and Prejudice (2015)" -> "Jane Austen/Pride and Prejudice (2015)"
            var expected = Path.Join("Jane Austen", "Pride and Prejudice (2015)");
            Assert.Equal(expected, result);
            Assert.DoesNotContain("Pride and Prejudice" + Path.DirectorySeparatorChar + "Pride and Prejudice", result);
        }
    }
}
