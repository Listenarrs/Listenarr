using System.Net;
using System.Text.Json;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class DelugeApiMock : BaseApiMock
    {
        public bool Authenticated { get; set; } = false;
        public bool DaemonConnected { get; set; } = true;
        public string? UpdateUiResponseOverride { get; set; }
        public bool FailRequests { get; set; } = false;

        public DelugeApiMock()
        {
            AddRoute("json", HandleRpc, HttpMethod.Post);
        }

        private async Task<HttpResponseMessage> HandleRpc(HttpRequestMessage request, CancellationToken ct)
        {
            if (FailRequests)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                string responseBody = method switch
                {
                    "auth.login" => """
                    {
                      "id": 1,
                      "result": true,
                      "error": null
                    }
                    """,
                    "web.connected" => $$"""
                    {
                      "id": 1,
                      "result": {{DaemonConnected.ToString().ToLowerInvariant()}},
                      "error": null
                    }
                    """,
                    "web.get_hosts" => """
                    {
                      "id": 1,
                      "result": [["host_id_1", "localhost", 58846, "connected"]],
                      "error": null
                    }
                    """,
                    "web.connect" => """
                    {
                      "id": 1,
                      "result": true,
                      "error": null
                    }
                    """,
                    "core.add_torrent_file" => """
                    {
                      "id": 1,
                      "result": "ABCDEF1234567890",
                      "error": null
                    }
                    """,
                    "core.add_torrent_magnet" => """
                    {
                      "id": 1,
                      "result": "ABCDEF1234567890",
                      "error": null
                    }
                    """,
                    "web.download_torrent_from_url" => """
                    {
                      "id": 1,
                      "result": "/tmp/torrent.torrent",
                      "error": null
                    }
                    """,
                    "web.add_torrents" => """
                    {
                      "id": 1,
                      "result": ["ABCDEF1234567890"],
                      "error": null
                    }
                    """,
                    "label.set_torrent" => """
                    {
                      "id": 1,
                      "result": true,
                      "error": null
                    }
                    """,
                    "core.remove_torrent" => """
                    {
                      "id": 1,
                      "result": true,
                      "error": null
                    }
                    """,
                    "web.update_ui" => UpdateUiResponseOverride ?? """
                    {
                      "id": 1,
                      "result": {
                        "torrents": {}
                      },
                      "error": null
                    }
                    """,
                    _ => """
                    {
                      "id": 1,
                      "result": null,
                      "error": null
                    }
                    """
                };

                return MockUtils.GetCannedResponse(responseBody);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
