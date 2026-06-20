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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    public class LibraryController_BulkUpdateTests : BaseTests
    {
        [Fact]
        public async Task BulkUpdate_ApplyRootMonitoredQuality_ReturnsPerIdResultsAndPersistsChanges()
        {
            // Arrange
            var controller = _provider.GetRequiredService<LibraryController>();

            var tempRoot = FileService.GetTempDirectory("bulk-update");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(tempRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());

            await _qualityProfileRepository.AddAsync(new QualityProfile
            {
                Id = 42,
                Name = "Test Profile",
                Qualities = new List<QualityDefinition>(),
                PreferredFormats = new List<string>(),
                PreferredLanguages = new List<string>(),
                MustContain = new List<string>(),
                MustNotContain = new List<string>()
            });

            var a1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book A",
                Authors = new List<string> { "Author A" },
                Monitored = false,
                QualityProfileId = null
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book B",
                Authors = new List<string> { "Author B" },
                Monitored = false,
                QualityProfileId = null
            });

            var request = new LibraryController.BulkUpdateRequest
            {
                Ids = new List<int> { a1.Id, 999999 },
                Updates = new Dictionary<string, object>
                {
                    { "monitored", true },
                    { "qualityProfileId", 42 },
                    { "rootFolder", tempRoot }
                }
            };

            // Act
            var actionResult = await controller.BulkUpdateAudiobooks(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult);

            var json = JsonSerializer.Serialize(ok.Value);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("results", out var resultsElem));
            Assert.Equal(2, resultsElem.GetArrayLength());

            var first = resultsElem[0];
            Assert.Equal(a1.Id, first.GetProperty("id").GetInt32());
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.True(first.GetProperty("errors").GetArrayLength() == 0);

            var second = resultsElem[1];
            Assert.Equal(999999, second.GetProperty("id").GetInt32());
            Assert.False(second.GetProperty("success").GetBoolean());
            Assert.True(second.GetProperty("errors").GetArrayLength() >= 1);

            var storedA1 = await _audiobookRepository.GetByIdAsync(a1.Id);
            Assert.NotNull(storedA1);
            Assert.True(storedA1.Monitored);
            Assert.Equal(42, storedA1.QualityProfileId);
            Assert.False(string.IsNullOrWhiteSpace(storedA1.BasePath));
            Assert.StartsWith(FileUtils.NormalizeStoredPath(tempRoot), storedA1.BasePath);
            Assert.Contains("Author A", storedA1.BasePath);
            Assert.Contains("Book A", storedA1.BasePath);

            var histories = await _historyRepository.GetByAudiobookIdAsync(a1.Id);
            Assert.True(histories.Count >= 1);
        }
    }
}
