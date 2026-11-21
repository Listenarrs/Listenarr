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
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Listenarr.Application.Configuration.Contracts;
using Listenarr.Application.SystemDiagnostics.Contracts;
using Listenarr.Domain.Configuration;
using Listenarr.FakeProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.BackendIntegrationTests;

/// <summary>
/// The whole application in process, on a SQLite file and real folders, driven only through its HTTP API.
/// Each test starts its own host from the same seed (four library books and two root folders added through
/// the API), so tests never share state. External APIs answer from the Listenarr.FakeProviders fixtures; ffprobe is the one
/// on PATH, so the import scan reads real tags.
/// </summary>
public sealed class BackendIntegrationHost : IAsyncDisposable
{
    public const string ImportRootName = "Backend Integration Import";
    public const string LibraryRootName = "Backend Integration Library";

    private static readonly SeedBook[] SeedBooks =
    [
        new("Twenty Thousand Leagues Under the Sea", "Jules Verne", "B01FKWL15A", "1870", "1870-06-20",
            "A voyage beneath the sea aboard the Nautilus.", ["Classics", "Science Fiction"], 600),
        new("Robinson Crusoe", "Daniel Defoe", "B002V5CW08", "1719", "1719-04-25",
            "A castaway survives on a remote island.", ["Classics", "Adventure"], 720),
        new("The Strange Case of Dr Jekyll and Mr Hyde", "Robert Louis Stevenson", "B076FF3QGP", "1886", "1886-01-05",
            "A London doctor's experiment reveals a darker self.", ["Classics", "Horror"], 240),
        new("Pride and Prejudice", "Jane Austen", "B071S17YLK", "1813", "1813-01-28",
            "Elizabeth Bennet navigates family, manners, and first impressions.", ["Classics", "Romance"], 660),
    ];

    private readonly string _databasePath;
    private Factory _factory;
    private string? _xsrfToken;

    private BackendIntegrationHost(string root)
    {
        ImportPath = Path.Join(root, "import");
        LibraryPath = Path.Join(root, "library");
        Directory.CreateDirectory(ImportPath);
        Directory.CreateDirectory(LibraryPath);
        _databasePath = Path.Join(root, "config", "listenarr.db");
        _factory = new Factory(_databasePath);
        Client = _factory.CreateClient();
    }

    public HttpClient Client { get; private set; }

    /// <summary>The folder of the "Backend Integration Import" root folder.</summary>
    public string ImportPath { get; }

    /// <summary>The folder of the "Backend Integration Library" root folder.</summary>
    public string LibraryPath { get; }

    /// <summary>
    /// Starts a host in <paramref name="root"/>; <paramref name="prepareFiles"/> writes the files the
    /// root folders hold before the seed (they are scanned only on request).
    /// </summary>
    public static async Task<BackendIntegrationHost> StartAsync(string root, Action<BackendIntegrationHost>? prepareFiles = null)
    {
        var host = new BackendIntegrationHost(root);
        try
        {
            prepareFiles?.Invoke(host);
            await host.SeedAsync();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task<HttpResponseMessage> GetAsync(string path) => await Client.GetAsync(path);

    public async Task<JsonNode> GetJsonAsync(string path)
    {
        using var response = await Client.GetAsync(path);
        return await ReadJsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary>Sends an unsafe request with the antiforgery header, as the frontend does.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        _xsrfToken ??= await FetchAntiforgeryTokenAsync();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);
        if (body != null)
        {
            request.Content = body is JsonNode node
                ? new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        }

        return await Client.SendAsync(request);
    }

    public async Task<JsonNode> SendJsonAsync(
        HttpMethod method,
        string path,
        object? body = null,
        HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var response = await SendAsync(method, path, body);
        return await ReadJsonAsync(response, expectedStatus);
    }

    /// <summary>Sends an unsafe request and asserts a 200, whatever the body.</summary>
    public async Task SendOkAsync(HttpMethod method, string path, object? body = null)
    {
        using var response = await SendAsync(method, path, body);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{method} {path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == expectedStatus,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}, expected {(int)expectedStatus}: {text}");
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("The response body is JSON null.");
    }

    public async Task<JsonArray> GetLibraryAsync() => (await GetJsonAsync("/api/v1/library")).AsArray();

    public async Task<JsonNode> GetRootFolderAsync(string name)
    {
        var folders = (await GetJsonAsync("/api/v1/rootfolders")).AsArray();
        return folders.SingleOrDefault(folder => (string?)folder?["name"] == name)
            ?? throw new InvalidOperationException($"The root folder '{name}' is missing.");
    }

    /// <summary>Restarts the app on the same database and folders, without re-seeding.</summary>
    public async Task<BackendIntegrationHost> RestartOnSameRootAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        _factory = new Factory(_databasePath);
        Client = _factory.CreateClient();
        _xsrfToken = null;
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<string> FetchAntiforgeryTokenAsync()
    {
        var token = (string?)(await GetJsonAsync("/api/v1/antiforgery/token"))["token"];
        Assert.False(string.IsNullOrWhiteSpace(token), "The API issued no antiforgery token.");
        return token!;
    }

    private async Task SeedAsync()
    {
        await SendJsonAsync(HttpMethod.Post, "/api/v1/qualityprofile", new
        {
            name = "Integration Default",
            isDefault = true,
        }, HttpStatusCode.Created);

        foreach (var book in SeedBooks)
        {
            // Adding with authors triggers author enrichment; set them afterwards so the seed stays offline.
            var added = await SendJsonAsync(HttpMethod.Post, "/api/v1/library/add", new
            {
                metadata = new
                {
                    title = book.Title,
                    authors = Array.Empty<string>(),
                    asin = book.Asin,
                    region = "us",
                    source = "Audible",
                    publishYear = book.PublishYear,
                    publishedDate = book.PublishedDate,
                    description = book.Description,
                    genres = book.Genres,
                    language = "english",
                    runtime = book.Runtime,
                },
                monitored = true,
                autoSearch = false,
            });
            var id = (int)added["audiobook"]!["id"]!;
            await SendOkAsync(HttpMethod.Put, $"/api/v1/library/{id}", new { authors = new[] { book.Author } });
        }

        foreach (var (name, path) in new[] { (ImportRootName, ImportPath), (LibraryRootName, LibraryPath) })
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/rootfolders", new
            {
                name,
                path,
                isDefault = false,
                caseSensitivityMode = 0,
            });
            Assert.True(response.IsSuccessStatusCode, $"Could not create root folder '{name}': {await response.Content.ReadAsStringAsync()}");
        }
    }

    /// <summary>The ffprobe on PATH, or null.</summary>
    public static string? FindFfprobe()
    {
        var name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Join(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private sealed record SeedBook(
        string Title,
        string Author,
        string Asin,
        string PublishYear,
        string PublishedDate,
        string Description,
        string[] Genres,
        int Runtime);

    /// <summary>
    /// The application on its own SQLite file, the unmatched scan worker running (the other hosted services
    /// off), no authentication, external APIs answered by the Listenarr.FakeProviders stub, and the ffprobe on PATH.
    /// </summary>
    private sealed class Factory(string databasePath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            // Host settings, not app configuration: Program reads them while it composes the services,
            // before ConfigureAppConfiguration callbacks apply.
            builder.UseSetting("Listenarr:SqliteDbPath", databasePath);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStartupConfigService>();
                var startupConfig = new Mock<IStartupConfigService>();
                startupConfig.Setup(service => service.GetConfig()).Returns(new StartupConfig { AuthenticationRequired = "false" });
                startupConfig.Setup(service => service.ReloadAsync()).Returns(Task.CompletedTask);
                startupConfig.Setup(service => service.SaveAsync(It.IsAny<StartupConfig>())).Returns(Task.CompletedTask);
                services.AddSingleton(startupConfig.Object);

                services.AddListenarrExternalApiTestStub();
                // Hosted services are disabled in the test environment, so the unmatched scan
                // worker that the tagged-file import test needs is not registered by the app.
                FakeProvidersHost.AddUnmatchedScanWorker(services);

                services.RemoveAll<IFfmpegService>();
                var ffprobe = FindFfprobe();
                var ffmpeg = new Mock<IFfmpegService>();
                ffmpeg.Setup(service => service.GetFfprobePathAsync()).ReturnsAsync(ffprobe);
                ffmpeg.Setup(service => service.EnsureFfprobeInstalledAsync()).ReturnsAsync(ffprobe);
                services.AddSingleton(ffmpeg.Object);
            });
        }
    }
}

/// <summary>Base of the backend integration tests: a fresh, seeded <see cref="BackendIntegrationHost"/> per test.</summary>
public abstract class BackendIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "listenarr-backend-integration", Guid.NewGuid().ToString("N"));
    private BackendIntegrationHost? _host;

    protected BackendIntegrationHost Host =>
        _host ?? throw new InvalidOperationException("The backend integration host has not started.");

    /// <summary>Writes files into the root folders before the host is seeded.</summary>
    protected virtual void PrepareFiles(BackendIntegrationHost host)
    {
    }

    public async Task InitializeAsync()
    {
        _host = await BackendIntegrationHost.StartAsync(_root, PrepareFiles);
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open; the temporary folder is cleaned by the OS.
        }
    }
}
