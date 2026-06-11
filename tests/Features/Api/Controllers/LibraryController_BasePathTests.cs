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
using Listenarr.Api.Controllers;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_BasePathTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_BasePathTests : BaseTests
    {
        private const string RootPath = "/server/mnt/drive/Audiobooks";
        private const string FileNamingPattern = "{Author}/{Series}/{Title}";

        private static readonly MethodInfo ComputeBaseDirectoryMethod =
            typeof(LibraryController).GetMethod("ComputeAudiobookBaseDirectoryFromPattern",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

        [Fact]
        [Trait("Method", "ComputeAudiobookBaseDirectoryFromPattern")]
        [Trait("Scenario", "NonSeriesBook_ReturnsCorrectPath")]
        public void ComputeAudiobookBaseDirectoryFromPattern_NonSeriesBook_ReturnsCorrectPath()
        {
            // Given
            var audiobook = new AudiobookBuilder()
                .WithTitle("The Buffalo Hunter Hunter")
                .WithAuthor("Stephen Graham Jones")
                .WithYear("2025")
                .Build();

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = (string)ComputeBaseDirectoryMethod.Invoke(controller, new object[] { audiobook, RootPath, FileNamingPattern });

            // Then
            Assert.Equal(Path.Join(RootPath, "Stephen Graham Jones", "The Buffalo Hunter Hunter"), result);
        }

        [Fact]
        [Trait("Method", "ComputeAudiobookBaseDirectoryFromPattern")]
        [Trait("Scenario", "SeriesBook_ReturnsCorrectPath")]
        public void ComputeAudiobookBaseDirectoryFromPattern_SeriesBook_ReturnsCorrectPath()
        {
            // Given
            var audiobook = new AudiobookBuilder()
                .WithTitle("The Gunslinger")
                .WithAuthor("Stephen King")
                .WithYear("1982")
                .WithSeries("The Dark Tower")
                .WithSeriesNumber("1")
                .Build();

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = (string)ComputeBaseDirectoryMethod.Invoke(controller, new object[] { audiobook, RootPath, FileNamingPattern });

            // Then
            Assert.Equal(Path.Join(RootPath, "Stephen King", "The Dark Tower", "The Gunslinger"), result);
        }
    }
}
