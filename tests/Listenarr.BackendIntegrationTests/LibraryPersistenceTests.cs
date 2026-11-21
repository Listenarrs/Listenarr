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
using System.Net;

using Xunit;

namespace Listenarr.BackendIntegrationTests;

[Trait("Name", "LibraryPersistenceTests")]
[Trait("Category", "BackendIntegration")]
public sealed class LibraryPersistenceTests : BackendIntegrationTests
{
    [Theory]
    [InlineData("authors", "monitoredAuthor")]
    [InlineData("series", "monitoredSeries")]
    public async Task UpdateMonitoringProfile_ChangesOnlyTheRequestedExistingMonitor(string collection, string monitorKey)
    {
        const string name = "Profile Update Review Fixture";
        var monitorPath = $"/api/v1/{collection}/monitoring";
        var statusPath = $"{monitorPath}/status?name={Uri.EscapeDataString(name)}&region=us&language=english";
        var profile = await Host.SendJsonAsync(HttpMethod.Post, "/api/v1/qualityprofile", new
        {
            name = "Replacement Profile",
            isDefault = false,
        }, HttpStatusCode.Created);
        var profileId = (int)profile["id"]!;
        var request = new { name, region = "us", language = "english" };

        var original = (await Host.SendJsonAsync(HttpMethod.Post, monitorPath, request))[monitorKey]!;
        var originalId = (int)original["id"]!;
        var persisted = (await Host.GetJsonAsync(statusPath))[monitorKey]!;
        var updatePath = $"{monitorPath}/{originalId}/quality-profile";
        var updated = await Host.SendJsonAsync(HttpMethod.Put, updatePath, new { qualityProfileId = profileId });
        Assert.Equal(originalId, (int)updated["id"]!);
        Assert.Equal(profileId, (int)updated["qualityProfileId"]!);
        Assert.Equal((string?)persisted["lastCheckedAt"], (string?)updated["lastCheckedAt"]);
        Assert.Equal((string?)persisted["lastSuccessfulSyncAt"], (string?)updated["lastSuccessfulSyncAt"]);

        using (var invalid = await Host.SendAsync(HttpMethod.Put, updatePath, new { qualityProfileId = int.MaxValue }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(profileId, (int)(await Host.GetJsonAsync(statusPath))[monitorKey]!["qualityProfileId"]!);

        await Host.SendOkAsync(HttpMethod.Delete, $"{monitorPath}/{originalId}");
        using (var stale = await Host.SendAsync(HttpMethod.Put, updatePath, new { qualityProfileId = profileId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        }
        Assert.False((bool)(await Host.GetJsonAsync(statusPath))["isMonitored"]!);

        var replacement = (await Host.SendJsonAsync(HttpMethod.Post, monitorPath, request))[monitorKey]!;
        var replacementId = (int)replacement["id"]!;
        Assert.NotEqual(originalId, replacementId);
        using (var stale = await Host.SendAsync(HttpMethod.Put, updatePath, new { qualityProfileId = profileId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        }
        var current = (await Host.GetJsonAsync(statusPath))[monitorKey]!;
        Assert.Equal(replacementId, (int)current["id"]!);
        Assert.Equal((int)replacement["qualityProfileId"]!, (int)current["qualityProfileId"]!);
    }

    [Fact]
    public async Task DeleteBook_RemovesItWhileTheOtherBooksStayReadable()
    {
        var books = await Host.GetLibraryAsync();
        Assert.Equal(4, books.Count);
        var deletedId = (int)books[0]!["id"]!;

        await Host.SendOkAsync(HttpMethod.Delete, $"/api/v1/library/{deletedId}?deleteFiles=false&deleteFolder=false");

        using (var deleted = await Host.GetAsync($"/api/v1/library/{deletedId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }

        foreach (var book in books.Skip(1))
        {
            var saved = await Host.GetJsonAsync($"/api/v1/library/{(int)book!["id"]!}");
            Assert.Equal((string?)book["title"], (string?)saved["title"]);
        }

        Assert.Equal(3, (await Host.GetLibraryAsync()).Count);

        await Host.RestartOnSameRootAsync();
        Assert.Equal(3, (await Host.GetLibraryAsync()).Count);
        using (var deletedAgain = await Host.GetAsync($"/api/v1/library/{deletedId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
        }
    }

    [Fact]
    public async Task EditMetadata_KeepsMonitoringAndQualityProfile()
    {
        const string subtitle = "Persisted backend integration subtitle";
        var bookId = (int)(await Host.GetLibraryAsync())[0]!["id"]!;
        var profile = await Host.SendJsonAsync(HttpMethod.Post, "/api/v1/qualityprofile", new
        {
            name = "Backend Integration Persistence Profile",
            isDefault = true,
        }, HttpStatusCode.Created);
        var profileId = (int)profile["id"]!;

        await Host.SendOkAsync(HttpMethod.Put, $"/api/v1/library/{bookId}", new { monitored = false, qualityProfileId = profileId });
        await Host.SendOkAsync(HttpMethod.Put, $"/api/v1/library/{bookId}", new { subtitle });

        var saved = await Host.GetJsonAsync($"/api/v1/library/{bookId}");
        Assert.False((bool)saved["monitored"]!);
        Assert.Equal(profileId, (int)saved["qualityProfileId"]!);
        Assert.Equal(subtitle, (string?)saved["subtitle"]);
    }
}
