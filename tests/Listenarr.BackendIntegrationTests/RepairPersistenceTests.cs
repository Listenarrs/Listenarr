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
using System.Text.Json.Nodes;

using Xunit;

namespace Listenarr.BackendIntegrationTests;

[Trait("Name", "RepairPersistenceTests")]
[Trait("Category", "BackendIntegration")]
public sealed class RepairPersistenceTests : BackendIntegrationTests
{
    // The record the fake Audible serves (tests/Listenarr.FakeProviders/Fixtures/Audible).
    private const string MatchAsin = "B005FRGT44";
    private const string MatchRegion = "us";
    private const string MatchTitle = "Ready Player One";
    private const string MatchAuthor = "Ernest Cline";

    // What a repair empties before the rescan, so the book keeps nothing of its old record.
    private static readonly object EmptiedRecord = new
    {
        subtitle = "",
        series = "",
        seriesNumber = "",
        seriesMemberships = Array.Empty<object>(),
        description = "",
        publisher = "",
        publishYear = "",
        publishedDate = "",
        language = "",
        version = "",
        edition = "",
        narrators = Array.Empty<string>(),
        genres = Array.Empty<string>(),
        isbn = Array.Empty<string>(),
    };

    [Fact]
    public async Task Repair_DeletedBookFails_NextSelectedBookIsRepairedAndPersisted()
    {
        var books = await Host.GetLibraryAsync();
        var deletedId = (int)books[0]!["id"]!;
        var repairedId = (int)books[1]!["id"]!;
        await Host.SendOkAsync(HttpMethod.Delete, $"/api/v1/library/{deletedId}?deleteFiles=false&deleteFolder=false");

        var failed = await RepairAsync(deletedId);
        Assert.Equal(("identifiers", HttpStatusCode.NotFound), (failed.Stage, failed.Status));

        var repaired = await RepairAsync(repairedId);
        Assert.Equal(("rescan", HttpStatusCode.OK), (repaired.Stage, repaired.Status));
        Assert.Equal(repairedId, (int)repaired.Body!["audiobookId"]!);
        Assert.Equal(MatchAsin, (string?)repaired.Body["asin"]);
        Assert.Equal(MatchRegion, (string?)repaired.Body["region"]);

        var identifiers = (await Host.GetJsonAsync($"/api/v1/library/{repairedId}/identifiers"))["identifiers"]!.AsArray();
        Assert.Single(identifiers, identifier =>
            string.Equals((string?)identifier!["value"], MatchAsin, StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)identifier["region"], MatchRegion, StringComparison.OrdinalIgnoreCase)
            && (bool)identifier["isPrimary"]!);
        var saved = await Host.GetJsonAsync($"/api/v1/library/{repairedId}");
        Assert.Equal(MatchTitle, (string?)saved["title"]);
        Assert.Equal(new[] { MatchAuthor }, saved["authors"]!.AsArray().Select(author => (string?)author));

        Assert.Equal(3, (await Host.GetLibraryAsync()).Count);
        using (var deleted = await Host.GetAsync($"/api/v1/library/{deletedId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }

        await Host.RestartOnSameRootAsync();
        var afterRestart = await Host.GetJsonAsync($"/api/v1/library/{repairedId}");
        Assert.Equal(MatchTitle, (string?)afterRestart["title"]);
        Assert.Equal(MatchAsin, (string?)afterRestart["asin"]);

        // Retrying the deleted book fails the same way.
        var retried = await RepairAsync(deletedId);
        Assert.Equal(("identifiers", HttpStatusCode.NotFound), (retried.Stage, retried.Status));
    }

    [Fact]
    public async Task Repair_RecordChange_KeepsMonitoringAndQualityProfile()
    {
        var bookId = (int)(await Host.GetLibraryAsync())[0]!["id"]!;
        var profile = await Host.SendJsonAsync(HttpMethod.Post, "/api/v1/qualityprofile", new
        {
            name = "Backend Integration Repair Profile",
            isDefault = true,
        }, HttpStatusCode.Created);
        var profileId = (int)profile["id"]!;
        await Host.SendOkAsync(HttpMethod.Put, $"/api/v1/library/{bookId}", new { monitored = false, qualityProfileId = profileId });

        var repaired = await RepairAsync(bookId);
        Assert.Equal(("rescan", HttpStatusCode.OK), (repaired.Stage, repaired.Status));

        var saved = await Host.GetJsonAsync($"/api/v1/library/{bookId}");
        Assert.Equal(MatchTitle, (string?)saved["title"]);
        Assert.False((bool)saved["monitored"]!);
        Assert.Equal(profileId, (int)saved["qualityProfileId"]!);
    }

    /// <summary>The three requests the Repair Books dialog sends for one book whose record changes.</summary>
    private async Task<(string Stage, HttpStatusCode Status, JsonNode? Body)> RepairAsync(int bookId)
    {
        using (var identifiers = await Host.SendAsync(HttpMethod.Put, $"/api/v1/library/{bookId}/identifiers", new
        {
            identifiers = new[]
            {
                new { type = "Asin", value = MatchAsin, region = MatchRegion, isPrimary = true, source = "Manual" },
            },
        }))
        {
            if (!identifiers.IsSuccessStatusCode)
            {
                return ("identifiers", identifiers.StatusCode, null);
            }
        }

        using (var metadata = await Host.SendAsync(HttpMethod.Put, $"/api/v1/library/{bookId}", EmptiedRecord))
        {
            if (!metadata.IsSuccessStatusCode)
            {
                return ("metadata", metadata.StatusCode, null);
            }
        }

        using var rescan = await Host.SendAsync(HttpMethod.Post, $"/api/v1/library/{bookId}/rescan-metadata");
        var text = await rescan.Content.ReadAsStringAsync();
        Assert.True(rescan.IsSuccessStatusCode, $"The rescan returned {(int)rescan.StatusCode}: {text}");
        return ("rescan", rescan.StatusCode, JsonNode.Parse(text));
    }
}
