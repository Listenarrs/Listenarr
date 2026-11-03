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
using System.Text;
using System.Text.Json;
using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportItemPathValidationTests")]
[Trait("Category", "Api")]
public sealed class ManualImportItemPathValidationTests : BaseTests
{
    [Theory]
    [InlineData("chapter..mp3")]
    [InlineData("Part 1... Intro.mp3")]
    [InlineData("Vol.", "file.mp3")]
    [InlineData("Initials A. B.", "chapter.mp3")]
    public void FullPath_AcceptsNamesThatOnlyContainDots(params string[] segments)
    {
        var expected = Path.Join([FileService.GetTempPath(), .. segments]);

        var item = new ManualImportItemDto { FullPath = expected };

        Assert.Equal(Path.GetFullPath(expected), item.FullPath);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("library/../outside.mp3")]
    [InlineData("library\\..\\outside.mp3")]
    [InlineData("library/..")]
    public void FullPath_RejectsParentDirectorySegmentsAsValidationFailure(string suffix)
    {
        var path = FileService.GetTempPath() + "/" + suffix;

        var exception = Assert.Throws<ApplicationValidationException>(
            () => new ManualImportItemDto { FullPath = path });

        Assert.Equal("manual_import_path_traversal", exception.Code);
    }

    [Fact]
    public async Task HttpPipeline_DottedFileName_ReachesPerItemProcessing()
    {
        var sourceDirectory = Path.Join(FileService.GetTempPath(), "Vol.");
        Directory.CreateDirectory(sourceDirectory);
        var sourceFile = Path.Join(sourceDirectory, "chapter..mp3");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3]);

        using var response = await PostManualImportAsync(sourceDirectory, sourceFile);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Audiobook with ID 4242 not found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpPipeline_ParentDirectorySegment_ReturnsBadRequest()
    {
        var sourceDirectory = FileService.GetTempPath();

        using var response = await PostManualImportAsync(
            sourceDirectory,
            sourceDirectory + "/library/../outside.mp3");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("manual_import_path_traversal", body, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostManualImportAsync(
        string sourceDirectory,
        string fullPath)
    {
        using var factory = new Listenarr.Tests.Mocks.ListenarrWebApplicationFactory();
        using var client = factory.CreateClient();
        var csrfToken = await GetAntiforgeryTokenAsync(client);
        var payload = JsonSerializer.Serialize(new
        {
            path = sourceDirectory,
            action = "None",
            items = new[]
            {
                new { fullPath, matchedAudiobookId = 4242 }
            }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/library/manual-import")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var tokenResponse = await client.GetAsync("/api/v1/antiforgery/token");
        tokenResponse.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await tokenResponse.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }
}
