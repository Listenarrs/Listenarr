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
using System.Text.Json;
using Listenarr.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_WantedFlagRegressionTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_WantedFlagRegressionTests : BaseTests
    {
        [Fact]
        [Trait("Method", "GetAll")]
        [Trait("Scenario", "TreatsDbFileRecordAsNotWanted_EvenIfPathIsMissing")]
        public async Task GetAll_TreatsDbFileRecordAsNotWanted_EvenIfPathIsMissing()
        {
            // Given
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Controller Book")
                .WithMonitored()
                .Build());

            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(book)
                .WithPath($@"Z:\definitely-missing\{Guid.NewGuid():N}.m4b")
                .WithSize(1024)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var actionResult = await controller.GetAll();
            var ok = Assert.IsType<OkObjectResult>(actionResult);

            // Then
            var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            var wanted = doc.RootElement
                .EnumerateArray()
                .Single(item => item.GetProperty("id").GetInt32() == book.Id)
                .GetProperty("wanted")
                .GetBoolean();

            Assert.False(wanted);
        }

        [Fact]
        [Trait("Method", "GetAll")]
        [Trait("Scenario", "TreatsLegacyFilePathAsNotWanted_WhenNoFileRowsExist")]
        public async Task GetAll_TreatsLegacyFilePathAsNotWanted_WhenNoFileRowsExist()
        {
            // Given
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Legacy FilePath Book")
                .WithMonitored()
                .WithFilePath(@"C:\legacy\book.m4b")
                .WithFileSize(2048)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var actionResult = await controller.GetAll();
            var ok = Assert.IsType<OkObjectResult>(actionResult);

            // Then
            var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            var item = doc.RootElement
                .EnumerateArray()
                .Single(element => element.GetProperty("id").GetInt32() == book.Id);

            Assert.False(item.GetProperty("wanted").GetBoolean());
            Assert.Equal("quality-match", item.GetProperty("status").GetString());
        }
    }
}
