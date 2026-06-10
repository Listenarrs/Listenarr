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
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;

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

        [Fact]
        [Trait("Method", "ComputeAudiobookBaseDirectoryFromPattern")]
        [Trait("Scenario", "SeriesBook_PatternWithoutSeries_OmitsSeriesFolder")]
        public void ComputeAudiobookBaseDirectoryFromPattern_SeriesBook_PatternWithoutSeries_OmitsSeriesFolder()
        {
            // Given a series book and a configured pattern that deliberately omits {Series}
            var audiobook = new AudiobookBuilder()
                .WithTitle("The Gunslinger")
                .WithAuthor("Stephen King")
                .WithYear("1982")
                .WithSeries("The Dark Tower")
                .WithSeriesNumber("1")
                .Build();

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = (string)ComputeBaseDirectoryMethod.Invoke(controller, new object[] { audiobook, RootPath, "{Author}/{Title}" });

            // Then the pattern is applied exactly — no series folder is injected
            Assert.Equal(Path.Join(RootPath, "Stephen King", "The Gunslinger"), result);
            Assert.DoesNotContain("The Dark Tower", result);
        }

        [Fact]
        [Trait("Method", "ComputeAudiobookBaseDirectoryFromPattern")]
        [Trait("Scenario", "SeriesBook_PatternWithYearAndAsin_OmitsSeriesFolder")]
        public void ComputeAudiobookBaseDirectoryFromPattern_SeriesBook_PatternWithYearAndAsin_OmitsSeriesFolder()
        {
            // Given a series book and a pattern with extra tokens but no {Series}
            var audiobook = new AudiobookBuilder()
                .WithTitle("Executive Orders")
                .WithAuthor("Tom Clancy")
                .WithYear("2010")
                .WithSeries("A Jack Ryan Novel (chronological order)")
                .WithSeriesNumber("8")
                .WithAsin("B004ESTSSO")
                .Build();

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = (string)ComputeBaseDirectoryMethod.Invoke(controller, new object[] { audiobook, RootPath, "{Author}/{Title} ({Year}) [{Asin}]" });

            // Then the remaining tokens still render and no series folder is injected
            Assert.Equal(Path.Join(RootPath, "Tom Clancy", "Executive Orders (2010) [B004ESTSSO]"), result);
            Assert.DoesNotContain("Jack Ryan", result);
        }

        [Fact]
        [Trait("Method", "ComputeAudiobookBaseDirectoryFromPattern")]
        [Trait("Scenario", "SlashlessPattern_AppliedExactly")]
        public void ComputeAudiobookBaseDirectoryFromPattern_SlashlessPattern_AppliedExactly()
        {
            // Given a deliberately flat folder pattern without directory separators
            var audiobook = new AudiobookBuilder()
                .WithTitle("The Buffalo Hunter Hunter")
                .WithAuthor("Stephen Graham Jones")
                .WithYear("2025")
                .Build();

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = (string)ComputeBaseDirectoryMethod.Invoke(controller, new object[] { audiobook, RootPath, "{Title}" });

            // Then the pattern is applied exactly — no implicit {Author} folder is prepended
            Assert.Equal(Path.Join(RootPath, "The Buffalo Hunter Hunter"), result);
            Assert.DoesNotContain("Stephen Graham Jones", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [Trait("Method", "ComputeAudiobookBaseDirectoryFromPattern")]
        [Trait("Scenario", "NoPatternConfigured_FallsBackToAuthorSeriesTitle")]
        public void ComputeAudiobookBaseDirectoryFromPattern_NoPatternConfigured_FallsBackToAuthorSeriesTitle(string? pattern)
        {
            // Given a series book and no configured pattern at all
            var audiobook = new AudiobookBuilder()
                .WithTitle("The Gunslinger")
                .WithAuthor("Stephen King")
                .WithSeries("The Dark Tower")
                .WithSeriesNumber("1")
                .Build();

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = (string)ComputeBaseDirectoryMethod.Invoke(controller, new object?[] { audiobook, RootPath, pattern })!;

            // Then the legacy default {Author}/{Series}/{Title} applies — aligned with the
            // orchestrator's no-pattern fallback (FileNamingService.BuildPath/BuildDirectory)
            Assert.Equal(Path.Join(RootPath, "Stephen King", "The Dark Tower", "The Gunslinger"), result);
        }

        [Fact]
        [Trait("Method", "ComputeAudiobookBaseDirectoryFromPattern")]
        [Trait("Scenario", "NonSeriesBook_KeepsSeriesNumberToken")]
        public void ComputeAudiobookBaseDirectoryFromPattern_NonSeriesBook_KeepsSeriesNumberToken()
        {
            // Given a book with no series title but a series number, and a pattern using {SeriesNumber}
            var audiobook = new AudiobookBuilder()
                .WithTitle("The Gunslinger")
                .WithAuthor("Stephen King")
                .WithSeriesNumber("3")
                .Build();

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = (string)ComputeBaseDirectoryMethod.Invoke(controller, new object[] { audiobook, RootPath, "{Author}/{SeriesNumber}/{Title}" });

            // Then {SeriesNumber} is preserved — it must not be stripped along with an empty {Series}
            Assert.Equal(Path.Join(RootPath, "Stephen King", "3", "The Gunslinger"), result);
        }
    }
}
