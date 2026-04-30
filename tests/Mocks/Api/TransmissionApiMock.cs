using System.Text.Json;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class TransmissionApiMock : BaseApiMock
    {
        public static readonly int SINGLE_FILE_TORRENT = 1;
        public static readonly int ANOTHER_SINGLE_FILE_TORRENT = 306;
        public static readonly int MULTI_FILE_TORRENT = 2;

        public TransmissionApiMock()
        {
            AddRoute("rpc", GetTorrent, HttpMethod.Post);
        }

        public static async Task<HttpResponseMessage> GetTorrent(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("method", out JsonElement methoPostElement))
            {
                string method = methoPostElement.GetString();
                if (string.Equals("torrent-get", method, StringComparison.OrdinalIgnoreCase))
                {
                    if (document.RootElement.TryGetProperty("arguments", out JsonElement argsElement) &&
                        argsElement.TryGetProperty("ids", out JsonElement idsElement))
                    {
                        var id = idsElement.EnumerateArray()
                            .Select(x => x.GetInt32())
                            .First();

                        if (id == SINGLE_FILE_TORRENT)
                        {
                            return SingleFileTorrentGet();
                        }
                        else if (id == MULTI_FILE_TORRENT)
                        {
                            return MultiFileTorrentGet();
                        }
                        else if (id == ANOTHER_SINGLE_FILE_TORRENT)
                        {
                            return AnotherSingleFileTorrentGet();
                        }
                    }

                    return MockUtils.GetCannedResponse("""
                    {
                        "arguments": {
                            "torrents": [
                                {
                                    "id": 1,
                                    "name": "Book.m4b",
                                    "downloadDir": "/downloads"
                                },
                                {
                                    "id": 2,
                                    "name": "Book Folder",
                                    "downloadDir": "/downloads",
                                    "files": [
                                        {
                                            "name": "Book Folder/chapter1.m4b"
                                        },
                                        {
                                            "name": "Book Folder/book.txt"
                                        }
                                    ]
                                },
                                {
                                    "id": 306,
                                    "name": "Isaac Asimov - Le Cycle de Fondation - Tome 3 - Seconde Fondation ",
                                    "downloadDir": "/downloads/complete/audiobooks"
                                }
                            ]
                        },
                        "result": "success",
                        "tag": 3
                    }
                    """);
                }
                else if (string.Equals("torrent-add", method, StringComparison.OrdinalIgnoreCase))
                {
                    return MockUtils.GetCannedResponse("""
                    {
                        "result": "success",
                        "arguments": {
                            "torrent-added": 
                            {
                                "id": 1,
                                "hashString": "HASH1",
                                "name": "Book"
                            }
                        }
                    }
                    """);
                }
                else if (string.Equals("session-get", method, StringComparison.OrdinalIgnoreCase))
                {
                    return MockUtils.GetCannedResponse("""
                    {
                        "result": "success",
                        "arguments": {}
                    }
                    """);
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage SingleFileTorrentGet()
        {
            return MockUtils.GetCannedResponse("""
            {
                "arguments": {
                    "torrents": [
                        {
                            "id": 1,
                            "name": "Book.m4b",
                            "downloadDir": "/downloads"
                        }
                    ]
                },
                "result": "success",
                "tag": 3
            }
            """);
        }

        private static HttpResponseMessage AnotherSingleFileTorrentGet()
        {

            return MockUtils.GetCannedResponse("""
            {
                "arguments": {
                    "torrents": [
                        {
                            "id": 306,
                            "name": "Isaac Asimov - Le Cycle de Fondation - Tome 3 - Seconde Fondation ",
                            "downloadDir": "/downloads/complete/audiobooks"
                        }
                    ]
                },
                "result": "success",
                "tag": 3
            }
            """);
        }

        private static HttpResponseMessage MultiFileTorrentGet()
        {
            return MockUtils.GetCannedResponse("""
            {
                "arguments": {
                    "torrents": [
                        {
                            "id": 2,
                            "name": "Book Folder",
                            "downloadDir": "/downloads",
                            "files": [
                                {
                                    "name": "Book Folder/chapter1.m4b"
                                },
                                {
                                    "name": "Book Folder/book.txt"
                                }
                            ]
                        }
                    ]
                },
                "result": "success",
                "tag": 3
            }
            """);
        }
    }
}
