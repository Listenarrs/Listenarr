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
using System.Diagnostics;
using System.Text.Json.Nodes;

using Xunit;

namespace Listenarr.BackendIntegrationTests;

/// <summary>
/// Library Import through the API on a real tagged file: scan (ffprobe reads the tags), ASIN lookup, add,
/// then move into the library root. Needs ffprobe on PATH.
/// </summary>
[Trait("Name", "AudioImportTests")]
[Trait("Category", "BackendIntegration")]
public sealed class AudioImportTests : BackendIntegrationTests
{
    // The record the fake Audible serves for this ASIN (tests/Listenarr.FakeProviders/Fixtures/Audible/B008DFUGCQ.json).
    private const string Asin = "B008DFUGCQ";
    private const string Title = "A Princess of Mars";
    private const string Author = "Edgar Rice Burroughs";

    private string _sourcePath = string.Empty;

    protected override void PrepareFiles(BackendIntegrationHost host)
    {
        Assert.True(
            BackendIntegrationHost.FindFfprobe() != null,
            "The import scan reads tags with ffprobe: install FFmpeg so that ffprobe is on PATH.");

        // One second of silence (see TagLibAudioTagWriterRoundTripTests), tagged as the book the scan must find.
        _sourcePath = Path.Join(host.ImportPath, Author, Title, $"{Title}.m4b");
        Directory.CreateDirectory(Path.GetDirectoryName(_sourcePath)!);
        File.Copy(Path.Join(AppContext.BaseDirectory, "Data", "the-time-machine-silence.m4b"), _sourcePath);
        using var file = TagLib.File.Create(_sourcePath);
        file.Tag.Title = Title;
        file.Tag.Album = Title;
        file.Tag.Performers = [Author];
        file.Tag.AlbumArtists = [Author];
        var appleTag = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, create: true);
        appleTag.SetDashBox("com.apple.iTunes", "ASIN", Asin);
        file.Save();
    }

    [Fact]
    public async Task ImportScan_MatchesTaggedFileAndMovesItIntoTheLibrary()
    {
        var importRootId = (int)(await Host.GetRootFolderAsync(BackendIntegrationHost.ImportRootName))["id"]!;
        var libraryRootPath = (string)(await Host.GetRootFolderAsync(BackendIntegrationHost.LibraryRootName))["path"]!;

        // Scan the import folder, as the Library Import page does.
        var jobId = (string)(await Host.SendJsonAsync(HttpMethod.Post, $"/api/v1/rootfolders/{importRootId}/scan-unmatched"))["jobId"]!;
        var scan = await WaitForScanAsync(jobId);
        var item = Assert.Single(scan["items"]!.AsArray())!;
        Assert.Equal(Asin, (string?)item["asin"]);
        Assert.Equal(_sourcePath, (string?)item["fullPath"]);

        // Look its ASIN up: the record is the match.
        var metadata = (await Host.GetJsonAsync($"/api/v1/metadata/{Asin}?region=us"))["metadata"]!;
        Assert.Equal(Title, (string?)metadata["title"]);
        Assert.Equal(Author, (string?)metadata["authors"]![0]!["name"]);

        // Add the matched book to the library, then move its file there.
        var beforeAdd = DateTime.UtcNow;
        var added = await Host.SendJsonAsync(HttpMethod.Post, "/api/v1/library/add", new
        {
            metadata = new { asin = Asin, title = Title, authors = new[] { Author }, region = "us", source = "Audible" },
            monitored = true,
            autoSearch = false,
            destinationPath = libraryRootPath,
        });
        var audiobookId = (int)added["audiobook"]!["id"]!;
        var addedAt = added["audiobook"]!["added"]!.GetValue<DateTime>();
        Assert.Equal(DateTimeKind.Utc, addedAt.Kind);
        Assert.InRange(addedAt, beforeAdd, DateTime.UtcNow);

        var sourceFiles = item["sourceFiles"]?.AsArray().Select(path => (string)path!).ToArray() ?? [(string)item["fullPath"]!];
        var import = await Host.SendJsonAsync(HttpMethod.Post, "/api/v1/library/manual-import", new
        {
            path = (string?)item["bookFolder"],
            mode = "interactive",
            action = "move",
            includeCompanionFiles = true,
            cleanupEmptySourceFolders = true,
            items = sourceFiles.Select(fullPath => new { fullPath, matchedAudiobookId = audiobookId }),
        });
        Assert.DoesNotContain(import["results"]!.AsArray(), result => !(bool)result!["success"]!);
        Assert.Equal(sourceFiles.Length, (int)import["importedCount"]!);

        // The library record points at the moved file, and the source is gone.
        var books = await Host.GetLibraryAsync();
        Assert.Equal(5, books.Count);
        var listItem = books.Single(book => (string?)book!["asin"] == Asin)!;
        Assert.Equal(audiobookId, (int)listItem["id"]!);
        Assert.Equal(addedAt, listItem["added"]!.GetValue<DateTime>());
        var saved = await Host.GetJsonAsync($"/api/v1/library/{audiobookId}");
        Assert.Equal(addedAt, saved["added"]!.GetValue<DateTime>());
        var storedPath = (string?)saved["files"]?.AsArray().FirstOrDefault()?["path"] ?? (string?)saved["filePath"] ?? string.Empty;
        var destinationPath = Path.IsPathRooted(storedPath) ? storedPath : Path.Join((string?)saved["basePath"], storedPath);
        Assert.StartsWith(Host.LibraryPath + Path.DirectorySeparatorChar, destinationPath, StringComparison.Ordinal);
        Assert.True(File.Exists(destinationPath), $"The moved file is missing at {destinationPath}.");
        Assert.False(File.Exists(_sourcePath), "The source file is still in the import folder.");
    }

    private async Task<JsonNode> WaitForScanAsync(string jobId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var scan = await Host.GetJsonAsync($"/api/v1/rootfolders/unmatched-results/{jobId}");
            var status = (string?)scan["status"];
            Assert.True(status != "Failed", $"The scan failed: {scan["error"]}");
            if (status == "Completed")
            {
                return scan;
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), $"The scan is still {status} after 30 seconds.");
            await Task.Delay(200);
        }
    }
}
