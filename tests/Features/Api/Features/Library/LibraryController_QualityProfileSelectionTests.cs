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
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "LibraryController_QualityProfileSelectionTests")]
    [Trait("Category", "LibraryController")]
    public sealed class LibraryController_QualityProfileSelectionTests : BaseTests
    {
        private const int MissingProfileId = 987654;

        [Fact]
        public async Task UpdateAudiobook_UseDefaultWithoutDefaultProfile_RejectsAndKeepsAssignment()
        {
            var assigned = await AddProfileAsync("Assigned", isDefault: false);
            var audiobook = await AddAudiobookAsync(assigned.Id);

            var result = await UpdateProfileAsync(audiobook.Id, -1);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(assigned.Id, (await GetFreshAudiobookAsync(audiobook.Id))!.QualityProfileId);
        }

        [Fact]
        public async Task UpdateAudiobook_UseDefaultWithDefaultProfile_AssignsDefault()
        {
            var assigned = await AddProfileAsync("Assigned", isDefault: false);
            var defaultProfile = await AddProfileAsync("Default", isDefault: true);
            var audiobook = await AddAudiobookAsync(assigned.Id);

            var result = await UpdateProfileAsync(audiobook.Id, -1);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(defaultProfile.Id, (await GetFreshAudiobookAsync(audiobook.Id))!.QualityProfileId);
        }

        [Fact]
        public async Task UpdateAudiobook_UnknownProfileId_RejectsAndKeepsAssignment()
        {
            var assigned = await AddProfileAsync("Assigned", isDefault: false);
            var audiobook = await AddAudiobookAsync(assigned.Id);

            var result = await UpdateProfileAsync(audiobook.Id, MissingProfileId);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(assigned.Id, (await GetFreshAudiobookAsync(audiobook.Id))!.QualityProfileId);
        }

        [Fact]
        public async Task UpdateAudiobook_ExistingProfileId_AssignsProfile()
        {
            var assigned = await AddProfileAsync("Assigned", isDefault: false);
            var target = await AddProfileAsync("Target", isDefault: false);
            var audiobook = await AddAudiobookAsync(assigned.Id);

            var result = await UpdateProfileAsync(audiobook.Id, target.Id);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(target.Id, (await GetFreshAudiobookAsync(audiobook.Id))!.QualityProfileId);
        }

        [Fact]
        public async Task BulkUpdate_UnknownProfileId_ReportsErrorAndKeepsAssignment()
        {
            var assigned = await AddProfileAsync("Assigned", isDefault: false);
            var audiobook = await AddAudiobookAsync(assigned.Id);

            var errors = await BulkUpdateProfileAsync(audiobook.Id, MissingProfileId);

            Assert.NotEmpty(errors);
            Assert.Equal(assigned.Id, (await GetFreshAudiobookAsync(audiobook.Id))!.QualityProfileId);
        }

        [Fact]
        public async Task BulkUpdate_UseDefaultWithoutDefaultProfile_ReportsErrorAndKeepsAssignment()
        {
            var assigned = await AddProfileAsync("Assigned", isDefault: false);
            var audiobook = await AddAudiobookAsync(assigned.Id);

            var errors = await BulkUpdateProfileAsync(audiobook.Id, -1);

            Assert.NotEmpty(errors);
            Assert.Equal(assigned.Id, (await GetFreshAudiobookAsync(audiobook.Id))!.QualityProfileId);
        }

        [Fact]
        public async Task BulkUpdate_UseDefaultWithDefaultProfile_AssignsDefaultProfileId()
        {
            var assigned = await AddProfileAsync("Assigned", isDefault: false);
            var defaultProfile = await AddProfileAsync("Default", isDefault: true);
            var audiobook = await AddAudiobookAsync(assigned.Id);

            var errors = await BulkUpdateProfileAsync(audiobook.Id, -1);

            Assert.Empty(errors);
            Assert.Equal(defaultProfile.Id, (await GetFreshAudiobookAsync(audiobook.Id))!.QualityProfileId);
        }

        private async Task<QualityProfile> AddProfileAsync(string name, bool isDefault) =>
            await _qualityProfileRepository.AddAsync(new QualityProfile
            {
                Name = name,
                IsDefault = isDefault,
                Qualities = new List<QualityDefinition>(),
                PreferredFormats = new List<string>(),
                PreferredLanguages = new List<string>(),
                MustContain = new List<string>(),
                MustNotContain = new List<string>()
            });

        private async Task<Audiobook> AddAudiobookAsync(int qualityProfileId) =>
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "A Princess of Mars",
                Authors = new List<string> { "Edgar Rice Burroughs" },
                Asin = "B008DFUGCQ",
                QualityProfileId = qualityProfileId
            });

        private Task<IActionResult> UpdateProfileAsync(int audiobookId, int qualityProfileId) =>
            _provider.GetRequiredService<LibraryController>().UpdateAudiobook(
                audiobookId,
                new AudiobookUpdateRequest { QualityProfileId = qualityProfileId });

        private async Task<List<string>> BulkUpdateProfileAsync(int audiobookId, int qualityProfileId)
        {
            var result = await _provider.GetRequiredService<LibraryController>().BulkUpdateAudiobooks(
                new LibraryController.BulkUpdateRequest
                {
                    Ids = new List<int> { audiobookId },
                    Updates = new Dictionary<string, object> { { "qualityProfileId", qualityProfileId } }
                });

            var ok = Assert.IsType<OkObjectResult>(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var item = Assert.Single(doc.RootElement.GetProperty("results").EnumerateArray());
            return item.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        }

        private async Task<Audiobook?> GetFreshAudiobookAsync(int id)
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAudiobookRepository>().GetByIdAsync(id);
        }
    }
}
