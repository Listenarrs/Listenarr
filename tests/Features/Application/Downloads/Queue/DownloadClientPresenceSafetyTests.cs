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
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Application.Downloads.Queue;

[Trait("Name", "DownloadClientPresenceSafetyTests")]
[Trait("Category", "DownloadOrphanCleanupService")]
public sealed class DownloadClientPresenceSafetyTests : BaseTests
{
    private const string Target = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static IEnumerable<object[]> Cases()
    {
        foreach (var client in new[] { "qbittorrent", "transmission", "sabnzbd", "nzbget" })
            foreach (var scenario in new[] { "present", "bad-telemetry", "absent", "unrelated-bad-present", "unrelated-bad-absent" })
                yield return [client, scenario];
        yield return ["qbittorrent", "ambiguous-id"];
        yield return ["transmission", "ambiguous-id"];
        yield return ["transmission", "conflicting-id-aliases"];
        foreach (var scenario in new[] { "older-history", "truncated-history", "history-without-total", "unreadable-history-id", "ambiguous-history-id" })
            yield return ["sabnzbd", scenario];
        foreach (var status in new[] { "WARNING/SPACE", "WARNING/REPAIRABLE", "WARNING/SCRIPT", "WARNING/HEALTH", "SUCCESS/ALL", "FAILURE/HEALTH", "DELETED/MANUAL" })
            yield return ["nzbget", status];
        foreach (var shape in new[] { "bad-history", "duplicate-array", "duplicate-data", "typed-id", "missing-status" })
            yield return ["nzbget", shape];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Cleanup_UsesClientIdentityWithoutInferringCompletion(string type, string scenario)
    {
        using var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        using var shutdown = new CancellationTokenSource();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requests = new ConcurrentQueue<string>();
        var server = Task.Run(async () =>
        {
            while (!shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (shutdown.IsCancellationRequested &&
                    ex is HttpListenerException or InvalidOperationException)
                {
                    // Windows can abort this accept before Stop updates IsListening.
                    return;
                }

                using var response = context.Response;
                using var reader = new StreamReader(context.Request.InputStream);
                var request = await reader.ReadToEndAsync();
                requests.Enqueue(context.Request.RawUrl + " " + request);
                var method = type == "nzbget" ? XDocument.Parse(request).Root!.Element("methodName")!.Value : string.Empty;
                var body = Response(type, scenario, context.Request, request, method);
                var bytes = Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
            }
        });

        var path = Path.Join(Path.GetTempPath(), $"presence-{Guid.NewGuid():N}.sqlite");
        var factory = new PresenceDbFactory(new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        Init(builder => builder.WithSingleton<IHttpClientFactory>(new PresenceHttpFactory()).WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory));
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        await CreateApplicationSettings();
        var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
            .WithHost("127.0.0.1").WithPort(port).WithoutSsl().WithUsername("review").WithPassword("review")
            .WithType(type).WithApiKey("fixture-key").WithSettings("category", "listenarr").Build());
        var tracked = new DownloadBuilder().WithId("presence-target").WithTitle("Target book").WithStatus(DownloadStatus.Downloading).WithStartDate(DateTime.UtcNow.AddMinutes(-10)).Build();
        tracked.DownloadClientId = client.Id;
        tracked.Metadata = new() { ["ClientDownloadId"] = type == "nzbget" ? "101" : Target };
        await _downloadRepository.AddAsync(tracked);
        var foreign = new DownloadBuilder().WithId("foreign-control").WithTitle("Other client").WithStatus(DownloadStatus.Downloading).WithStartDate(DateTime.UtcNow.AddMinutes(-10)).Build();
        foreign.DownloadClientId = "foreign-client";
        foreign.Metadata = new() { ["ClientDownloadId"] = "foreign" };
        await _downloadRepository.AddAsync(foreign);
        try
        {
            await _provider.GetRequiredService<DownloadMonitorProcessor>().RunCycleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(25));
            var stored = await _downloadRepository.GetByIdAsync(tracked.Id);
            var shouldRemove = scenario is "absent" or "unrelated-bad-absent" or "DELETED/MANUAL";
            Assert.Equal(!shouldRemove, stored != null);
            Assert.NotNull(await _downloadRepository.GetByIdAsync(foreign.Id));
            if (type == "sabnzbd" && scenario.Contains("history", StringComparison.Ordinal))
            {
                Assert.Contains(requests, request => request.Contains("nzo_ids=" + Target, StringComparison.Ordinal));
            }
            if (scenario.StartsWith("WARNING/", StringComparison.Ordinal))
            {
                Assert.Equal(DownloadStatus.Downloading, stored?.Status);
            }
        }
        finally
        {
            shutdown.Cancel();
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static string Response(string type, string scenario, HttpListenerRequest request, string requestBody, string method)
    {
        if (type == "qbittorrent" && request.RawUrl!.Contains("auth/login")) return "Ok.";
        if (type == "qbittorrent" && request.RawUrl!.Contains("torrents/files")) return "[]";
        var historyCase = scenario.Contains('/') || scenario is "bad-history" or "duplicate-array" or "duplicate-data" or "typed-id" or "missing-status";
        if (type == "nzbget" && method == "history")
        {
            if (!historyCase) return Xml("<array><data/></array>");
            var entry = NzbEntry("101", "listenarr", scenario.Contains('/') ? scenario : "SUCCESS/ALL");
            if (scenario == "bad-history") entry = "<value><string>unreadable</string></value>";
            if (scenario == "duplicate-array") return Xml("<array><data/></array><array><data>" + entry + "</data></array>");
            if (scenario == "duplicate-data") return Xml("<array><data/><data>" + entry + "</data></array>");
            if (scenario == "typed-id") entry = entry.Replace("<i4>101</i4>", "<array><data><value><i4>101</i4></value></data></array>");
            if (scenario == "missing-status") entry = entry.Replace(Member("Status", "SUCCESS/ALL"), "");
            return Xml("<array><data>" + entry + "</data></array>");
        }
        if (type == "sabnzbd" && request.QueryString["mode"] == "history")
        {
            if (request.QueryString["nzo_ids"] == Target)
            {
                return scenario switch
                {
                    "older-history" => JsonSerializer.Serialize(new { history = new { slots = new[] { new { nzo_id = Target, storage = 42 } }, noofslots = 1 } }),
                    "truncated-history" => """{"history":{"slots":[{"nzo_id":"other-job"}],"noofslots":101}}""",
                    "history-without-total" => """{"history":{"slots":[]}}""",
                    "unreadable-history-id" => """{"history":{"slots":[{"nzo_id":42}],"noofslots":1}}""",
                    "ambiguous-history-id" => $$$"""{"history":{"slots":[{"nzo_id":"{{{Target}}}","nzo_id":"other-job"}],"noofslots":1}}""",
                    _ => """{"history":{"slots":[],"noofslots":0}}"""
                };
            }

            return """{"history":{"slots":[],"noofslots":0}}""";
        }
        if (type == "nzbget" && method != "listgroups") return Xml("<struct/>");
        if (type == "qbittorrent" && scenario == "ambiguous-id" &&
            request.QueryString["category"] == null && request.QueryString["hashes"] == Target)
        {
            return $$"""[{"hash":"{{Target}}","hash":"{{Other}}"}]""";
        }
        if (type == "transmission" && !requestBody.Contains("percentDone", StringComparison.Ordinal))
        {
            if (scenario == "ambiguous-id")
            {
                return $$$"""{"result":"success","arguments":{"torrents":[{"id":7,"hashString":"{{{Target}}}","hashString":"{{{Other}}}"}]}}""";
            }
            if (scenario == "conflicting-id-aliases")
            {
                return $$$"""{"result":"success","arguments":{"torrents":[{"id":7,"hashString":"{{{Target}}}","hash_string":"{{{Other}}}"}]}}""";
            }
        }

        var entries = new List<string>();
        if (scenario != "absent" && scenario != "unrelated-bad-absent" && !historyCase &&
            scenario is not ("ambiguous-id" or "conflicting-id-aliases") &&
            !(type == "sabnzbd" && scenario.Contains("history", StringComparison.Ordinal)))
            entries.Add(ActiveEntry(type, type == "nzbget" ? "101" : Target, scenario == "bad-telemetry"));
        if (scenario.StartsWith("unrelated-bad-", StringComparison.Ordinal))
            entries.Add(ActiveEntry(type, type == "nzbget" ? "102" : Other, true));
        if (type == "qbittorrent")
        {
            if (!string.IsNullOrEmpty(request.QueryString["category"])) entries.Clear();
            var hashes = request.QueryString["hashes"]?.Split('|').ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (hashes != null) entries = entries.Where(entry => hashes.Contains(JsonDocument.Parse(entry).RootElement.GetProperty("hash").GetString()!)).ToList();
        }
        var joined = string.Join(type == "nzbget" ? "" : ",", entries);
        return type switch
        {
            "qbittorrent" => "[" + joined + "]",
            "transmission" => "{\"result\":\"success\",\"arguments\":{\"torrents\":[" + joined + "]}}",
            "sabnzbd" => "{\"queue\":{\"speed\":\"0\",\"slots\":[" + joined + "]}}",
            _ => Xml("<array><data>" + joined + "</data></array>")
        };
    }

    private static string ActiveEntry(string type, string id, bool bad)
    {
        return type switch
        {
            "qbittorrent" => JsonSerializer.Serialize(new { hash = id, name = "Target book", state = "downloading", progress = 0.5, size = 1000, downloaded = 500, category = "other" }).Replace("\"size\":1000", bad ? "\"size\":{}" : "\"size\":1000"),
            "transmission" => JsonSerializer.Serialize(new { id = id == Target ? 7 : 8, hashString = id, name = "Target book", status = 4, totalSize = 1000, leftUntilDone = 500, percentDone = 0.5, eta = 60, labels = new[] { "other" } }).Replace("\"eta\":60", bad ? "\"eta\":null" : "\"eta\":60"),
            "sabnzbd" => JsonSerializer.Serialize(new { nzo_id = id, filename = "Target book", cat = "other", status = "Downloading", storage = (object)(bad ? 42 : ""), mb = "100", mbleft = "50" }),
            _ => NzbEntry(id, "other", "DOWNLOADING").Replace("<int>100</int>", bad ? "<double>1e300</double>" : "<int>100</int>")
        };
    }

    private static string NzbEntry(string id, string category, string status) => "<value><struct><member><name>NZBID</name><value><i4>" + id + "</i4></value></member><member><name>LastID</name><value><i4>" + id + "</i4></value></member>" + Member("NZBName", "Target book") + Member("Category", category) + Member("Status", status) + "<member><name>FileSizeMB</name><value><int>100</int></value></member><member><name>RemainingSizeMB</name><value><int>50</int></value></member></struct></value>";
    private static string Member(string name, string value) => new XElement("member", new XElement("name", name), new XElement("value", new XElement("string", value))).ToString(SaveOptions.DisableFormatting);
    private static string Xml(string value) => "<methodResponse><params><param><value>" + value + "</value></param></params></methodResponse>";
    private sealed class PresenceDbFactory(DbContextOptions<ListenArrDbContext> options) : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
    }
    private sealed class PresenceHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(3) };
    }
}
