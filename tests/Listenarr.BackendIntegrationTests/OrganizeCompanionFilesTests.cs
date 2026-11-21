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
using System.Text.Json.Nodes;

using Xunit;

namespace Listenarr.BackendIntegrationTests;

/// <summary>
/// Organize (rename preview, then execute) of a book whose folder changes, through the API, on real
/// folders: the companions left in the previous folder (cover, metadata) follow the book only when that
/// folder held this book alone. Each scenario imports a book into a folder of the library root, lays
/// out the previous folder, then organizes the book into the "{Author}/{Title}" folder.
/// </summary>
[Trait("Name", "OrganizeCompanionFilesTests")]
[Trait("Category", "BackendIntegration")]
public sealed class OrganizeCompanionFilesTests : BackendIntegrationTests
{
    private const string SettingsPath = "/api/v1/configuration/settings";
    private const string Title = "The Organized Book";
    private const string Author = "Ada Organizer";

    [Fact]
    public async Task Organize_BookAloneInItsFolder_CarriesCoverAndMetadataToTheNewFolder()
    {
        await UseNamingAsync(importBlacklist: [".nfo"]);
        var book = await AddAndImportBookAsync(Title, Author, "B0ORGANIZ1", Path.Join(Host.LibraryPath, "Inbox"));
        WriteFiles(book.Folder, new()
        {
            ["cover.jpg"] = "the cover",
            ["metadata.json"] = "{\"title\":\"The Organized Book\"}",
            [".hidden"] = "hidden",
            ["notes.nfo"] = "blacklisted",
        });

        var result = await OrganizeAsync(book.Id);

        var newFolder = Path.Join(Host.LibraryPath, Author, Title);
        Assert.True((bool)result["success"]!, result.ToJsonString());
        Assert.True(File.Exists(Path.Join(newFolder, $"{Title}.m4b")));
        Assert.Equal("the cover", File.ReadAllText(Path.Join(newFolder, "cover.jpg")));
        Assert.Equal("{\"title\":\"The Organized Book\"}", File.ReadAllText(Path.Join(newFolder, "metadata.json")));
        Assert.False(File.Exists(Path.Join(book.Folder, "cover.jpg")));
        Assert.False(File.Exists(Path.Join(book.Folder, "metadata.json")));

        // Hidden and blacklisted files stay, so the previous folder is kept with them.
        Assert.Equal("hidden", File.ReadAllText(Path.Join(book.Folder, ".hidden")));
        Assert.Equal("blacklisted", File.ReadAllText(Path.Join(book.Folder, "notes.nfo")));
        Assert.False(File.Exists(Path.Join(newFolder, ".hidden")));
        Assert.False(File.Exists(Path.Join(newFolder, "notes.nfo")));

        var saved = await Host.GetJsonAsync($"/api/v1/library/{book.Id}");
        Assert.Equal(Path.Join(newFolder, $"{Title}.m4b"), (string?)saved["files"]![0]!["path"]);
    }

    [Theory]
    [InlineData("other.mp4")]
    [InlineData("Disc 9/stray.mp3")]
    public async Task Organize_UntrackedAudioLeftInThePreviousFolder_KeepsTheCompanionsThere(string leftover)
    {
        await UseNamingAsync();
        var book = await AddAndImportBookAsync(Title, Author, "B0ORGANIZ2", Path.Join(Host.LibraryPath, "Shared"));
        WriteFiles(book.Folder, new()
        {
            ["cover.jpg"] = "whose cover",
            [leftover] = "audio no book tracks",
        });

        var result = await OrganizeAsync(book.Id);

        var newFolder = Path.Join(Host.LibraryPath, Author, Title);
        Assert.True((bool)result["success"]!, result.ToJsonString());
        Assert.True(File.Exists(Path.Join(newFolder, $"{Title}.m4b")));
        Assert.Equal("whose cover", File.ReadAllText(Path.Join(book.Folder, "cover.jpg")));
        Assert.Equal("audio no book tracks", File.ReadAllText(Path.Join(book.Folder, leftover)));
        Assert.False(File.Exists(Path.Join(newFolder, "cover.jpg")));
    }

    [Fact]
    public async Task Organize_AnotherBookImportedInASubfolder_KeepsTheSharedCompanionsAndTheOtherBook()
    {
        await UseNamingAsync();
        var shared = Path.Join(Host.LibraryPath, "Shared");
        var book = await AddAndImportBookAsync(Title, Author, "B0ORGANIZ3", shared);
        var other = await AddAndImportBookAsync("The Bonus Book", "Bo Nus", "B0ORGANIZ4", Path.Join(shared, "Bonus"));
        WriteFiles(shared, new() { ["cover.jpg"] = "shared cover" });

        var result = await OrganizeAsync(book.Id);

        var newFolder = Path.Join(Host.LibraryPath, Author, Title);
        Assert.True((bool)result["success"]!, result.ToJsonString());
        Assert.True(File.Exists(Path.Join(newFolder, $"{Title}.m4b")));
        Assert.Equal("shared cover", File.ReadAllText(Path.Join(shared, "cover.jpg")));
        Assert.False(File.Exists(Path.Join(newFolder, "cover.jpg")));

        Assert.True(File.Exists(other.AudioPath), $"The other book's file is gone from {other.AudioPath}.");
        var otherSaved = await Host.GetJsonAsync($"/api/v1/library/{other.Id}");
        Assert.Equal(other.AudioPath, (string?)otherSaved["files"]![0]!["path"]);
    }

    [Fact]
    public async Task Organize_AnotherBookAssignedBelowThePreviousFolder_KeepsTheCompanionsEvenWithoutItsFiles()
    {
        await UseNamingAsync();
        var shared = Path.Join(Host.LibraryPath, "Shared");
        var book = await AddAndImportBookAsync(Title, Author, "B0ORGANIZ5", shared);
        // Only the database knows about this book: its folder holds nothing yet.
        await AddBookAsync("The Planned Book", "Plan Ned", "B0ORGANIZ6", Path.Join(shared, "Planned"));
        WriteFiles(shared, new() { ["cover.jpg"] = "shared cover" });

        var result = await OrganizeAsync(book.Id);

        var newFolder = Path.Join(Host.LibraryPath, Author, Title);
        Assert.True((bool)result["success"]!, result.ToJsonString());
        Assert.True(File.Exists(Path.Join(newFolder, $"{Title}.m4b")));
        Assert.Equal("shared cover", File.ReadAllText(Path.Join(shared, "cover.jpg")));
        Assert.False(File.Exists(Path.Join(newFolder, "cover.jpg")));
    }

    [Fact]
    public async Task Organize_AnotherBookInASiblingFolder_DoesNotHoldBackTheCompanions()
    {
        await UseNamingAsync();
        var book = await AddAndImportBookAsync(Title, Author, "B0ORGANIZ7", Path.Join(Host.LibraryPath, "Mine"));
        var sibling = await AddAndImportBookAsync("The Sibling Book", "Sib Ling", "B0ORGANIZ8", Path.Join(Host.LibraryPath, "Sibling"));
        WriteFiles(book.Folder, new() { ["cover.jpg"] = "my cover" });
        WriteFiles(Path.GetDirectoryName(sibling.AudioPath)!, new() { ["cover.jpg"] = "sibling cover" });

        var result = await OrganizeAsync(book.Id);

        var newFolder = Path.Join(Host.LibraryPath, Author, Title);
        Assert.True((bool)result["success"]!, result.ToJsonString());
        Assert.Equal("my cover", File.ReadAllText(Path.Join(newFolder, "cover.jpg")));
        Assert.False(File.Exists(Path.Join(book.Folder, "cover.jpg")));
        Assert.Equal("sibling cover", File.ReadAllText(Path.Join(Path.GetDirectoryName(sibling.AudioPath)!, "cover.jpg")));
    }

    private sealed record ImportedBook(int Id, string Folder, string AudioPath);

    /// <summary>Pins the naming the organize targets, and the import blacklist.</summary>
    private async Task UseNamingAsync(string[]? importBlacklist = null)
    {
        var settings = (await Host.GetJsonAsync(SettingsPath)).AsObject();
        settings["folderNamingPattern"] = "{Author}/{Title}";
        settings["fileNamingPattern"] = "{Title}";
        settings["importBlacklistExtensions"] = new JsonArray((importBlacklist ?? []).Select(extension => (JsonNode?)extension).ToArray());
        await Host.SendJsonAsync(HttpMethod.Post, SettingsPath, settings);
    }

    private async Task<int> AddBookAsync(string title, string author, string asin, string folder)
    {
        // Added without authors, then given them, so the add stays offline (as the seed does).
        var added = await Host.SendJsonAsync(HttpMethod.Post, "/api/v1/library/add", new
        {
            metadata = new { title, authors = Array.Empty<string>(), asin, region = "us", source = "Audible" },
            monitored = false,
            autoSearch = false,
            destinationPath = folder,
        });
        var id = (int)added["audiobook"]!["id"]!;
        await Host.SendOkAsync(HttpMethod.Put, $"/api/v1/library/{id}", new { authors = new[] { author } });
        return id;
    }

    /// <summary>Adds a book assigned to <paramref name="folder"/> and imports one audio file into it.</summary>
    private async Task<ImportedBook> AddAndImportBookAsync(string title, string author, string asin, string folder)
    {
        var id = await AddBookAsync(title, author, asin, folder);
        var source = Path.Join(Host.ImportPath, asin);
        Directory.CreateDirectory(source);
        var sourceFile = Path.Join(source, "track.m4b");
        File.Copy(Path.Join(AppContext.BaseDirectory, "Data", "the-time-machine-silence.m4b"), sourceFile);

        var import = await Host.SendJsonAsync(HttpMethod.Post, "/api/v1/library/manual-import", new
        {
            path = source,
            mode = "interactive",
            action = "move",
            includeCompanionFiles = false,
            cleanupEmptySourceFolders = true,
            items = new[] { new { fullPath = sourceFile, matchedAudiobookId = id } },
        });
        var imported = Assert.Single(import["results"]!.AsArray())!;
        Assert.True((bool)imported["success"]!, imported.ToJsonString());
        var audioPath = (string)imported["destinationPath"]!;
        Assert.Equal(folder, Path.GetDirectoryName(audioPath));
        return new ImportedBook(id, folder, audioPath);
    }

    private static void WriteFiles(string folder, Dictionary<string, string> files)
    {
        foreach (var (name, content) in files)
        {
            var path = Path.Join(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    /// <summary>Previews the organize of one book, checks it changes folder, and executes it as previewed.</summary>
    private async Task<JsonNode> OrganizeAsync(int audiobookId)
    {
        var preview = await Host.SendJsonAsync(HttpMethod.Post, $"/api/v1/library/{audiobookId}/rename/preview");
        Assert.True((bool)preview["folderChanged"]!, preview.ToJsonString());
        return await Host.SendJsonAsync(HttpMethod.Post, $"/api/v1/library/{audiobookId}/rename", preview);
    }
}
