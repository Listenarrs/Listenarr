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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Name", "LibraryController_BulkUpdateTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_BulkUpdateTests : BaseTests
    {
        [Fact]
        public async Task BulkUpdate_ApplyRootMonitoredQuality_ReturnsPerIdResultsAndPersistsChanges()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-bulk");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithFileNamingPattern("{Title}")
                .Build());

            var qualityProfile = await _qualityProfileRepository.AddAsync(new QualityProfileBuilder()
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Book A")
                .WithAuthor("Author A")
                .Build());

            var missingId = audiobook.Id + 999999;
            var request = new LibraryController.BulkUpdateRequest
            {
                Ids = [audiobook.Id, missingId],
                Updates = new Dictionary<string, object>
                {
                    { "monitored", true },
                    { "qualityProfileId", qualityProfile.Id },
                    { "rootFolder", outputRoot }
                }
            };

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.BulkUpdateAudiobooks(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("results", out var resultsElem));
            Assert.Equal(2, resultsElem.GetArrayLength());

            var first = resultsElem[0];
            Assert.Equal(audiobook.Id, first.GetProperty("id").GetInt32());
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.True(first.GetProperty("errors").GetArrayLength() == 0, json);

            var second = resultsElem[1];
            Assert.Equal(missingId, second.GetProperty("id").GetInt32());
            Assert.False(second.GetProperty("success").GetBoolean());
            Assert.True(second.GetProperty("errors").GetArrayLength() >= 1);

            var stored = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.True(stored.Monitored);
            Assert.Equal(qualityProfile.Id, stored.QualityProfileId);
            Assert.False(string.IsNullOrWhiteSpace(stored.BasePath));
            Assert.StartsWith(outputRoot, stored.BasePath);
            Assert.Contains("Author A", stored.BasePath);
            Assert.Contains("Book A", stored.BasePath);

            var histories = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.NotEmpty(histories);
        }

        /// <summary>
        /// Bulk root changes should persist the same base path that preview reports for empty folder patterns.
        /// </summary>
        [Fact]
        public async Task BulkUpdate_EmptyFolderPattern_SetsBasePathToRootFolder()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-bulk-empty-folder-pattern");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithFolderNamingPattern(string.Empty)
                .WithFileNamingPattern("{Title}")
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Book A")
                .WithAuthor("Author A")
                .Build());

            var request = new LibraryController.BulkUpdateRequest
            {
                Ids = [audiobook.Id],
                Updates = new Dictionary<string, object>
                {
                    { "rootFolder", outputRoot }
                }
            };

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.BulkUpdateAudiobooks(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var doc = JsonDocument.Parse(json);
            var result = Assert.Single(doc.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean(), json);

            var stored = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.Equal(outputRoot, stored.BasePath);
            Assert.DoesNotContain("Unknown", stored.BasePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Book A", stored.BasePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Author A", stored.BasePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
