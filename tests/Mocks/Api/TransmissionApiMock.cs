using System.Text.Json;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class TransmissionApiMock : BaseApiMock
    {
        public static readonly int SINGLE_FILE_TORRENT = 1;
        public static readonly int ANOTHER_SINGLE_FILE_TORRENT = 306;
        public static readonly int MULTI_FILE_TORRENT = 2;
        public static readonly int WHITESPACE_FOLDER_TORRENT = 528;

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
                        else if (id == WHITESPACE_FOLDER_TORRENT)
                        {
                            return WhitespaceFolderTorrentGet();
                        }
                    }

                    var response = """
                    {
                        "arguments": {
                            "torrents": [
                                {
                                    "id": 1,
                                    "name": "Book.m4b",
                                    "downloadDir": "{{DIR}}"
                                },
                                {
                                    "id": 2,
                                    "name": "Book Folder",
                                    "downloadDir": "{{DIR}}",
                                    "files": [
                                        {
                                            "name": "{{FILE1}}"
                                        },
                                        {
                                            "name": "{{FILE2}}"
                                        }
                                    ]
                                },
                                {
                                    "id": 306,
                                    "name": "Isaac Asimov - Le Cycle de Fondation - Tome 3 - Seconde Fondation ",
                                    "downloadDir": "{{REMOTE_PATH}}"
                                }
                            ]
                        },
                        "result": "success",
                        "tag": 3
                    }
                    """;
                    response = MockUtils.PutPathInResponse(response, "{{REMOTE_PATH}}", FileUtils.GetAbsolutePath("downloads", "complete", "audiobooks"));
                    response = MockUtils.PutPathInResponse(response, "{{DIR}}", FileUtils.GetAbsolutePath("downloads"));
                    response = MockUtils.PutPathInResponse(response, "{{FILE1}}", Path.Join("Book Folder", "chapter1.m4b"));
                    response = MockUtils.PutPathInResponse(response, "{{FILE2}}", Path.Join("Book Folder", "book.txt"));
                    return MockUtils.GetCannedResponse(response);
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
                else if (string.Equals("torrent-remove", method, StringComparison.OrdinalIgnoreCase))
                {
                    return MockUtils.GetCannedResponse("""
                    {
                        "result": "success",
                        "arguments": {},
                        "tag": 2
                    }
                    """);
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage SingleFileTorrentGet()
        {
            var response = """
            {
                "arguments": {
                    "torrents": [
                        {
                            "id": 1,
                            "name": "Book.m4b",
                            "downloadDir": "{{DIR}}"
                        }
                    ]
                },
                "result": "success",
                "tag": 3
            }
            """;
            response = MockUtils.PutPathInResponse(response, "{{DIR}}", FileUtils.GetAbsolutePath("downloads"));
            return MockUtils.GetCannedResponse(response);
        }

        private static HttpResponseMessage AnotherSingleFileTorrentGet()
        {
            var response = """
            {
                "arguments": {
                    "torrents": [
                        {
                            "id": 306,
                            "name": "Isaac Asimov - Le Cycle de Fondation - Tome 3 - Seconde Fondation ",
                            "downloadDir": "{{REMOTE_PATH}}"
                        }
                    ]
                },
                "result": "success",
                "tag": 3
            }
            """;
            response = MockUtils.PutPathInResponse(response, "{{REMOTE_PATH}}", FileUtils.GetAbsolutePath("downloads", "complete", "audiobooks"));
            return MockUtils.GetCannedResponse(response);
        }

        private static HttpResponseMessage MultiFileTorrentGet()
        {
            var response = """
            {
                "arguments": {
                    "torrents": [
                        {
                            "id": 2,
                            "name": "Book Folder",
                            "downloadDir": "{{DIR}}",
                            "files": [
                                {
                                    "name": "{{FILE1}}"
                                },
                                {
                                    "name": "{{FILE2}}"
                                }
                            ]
                        }
                    ]
                },
                "result": "success",
                "tag": 3
            }
            """;
            response = MockUtils.PutPathInResponse(response, "{{DIR}}", FileUtils.GetAbsolutePath("downloads"));
            response = MockUtils.PutPathInResponse(response, "{{FILE1}}", Path.Join("Book Folder", "chapter1.m4b"));
            response = MockUtils.PutPathInResponse(response, "{{FILE2}}", Path.Join("Book Folder", "book.txt"));
            return MockUtils.GetCannedResponse(response);
        }

        private static HttpResponseMessage WhitespaceFolderTorrentGet()
        {
            var response = """
            {
                "arguments": {
                    "torrents": [
                        {
                            "id": 528,
                            "name": " Book Folder ",
                            "downloadDir": "{{DIR}}",
                            "files": [
                                {
                                    "name": "{{FILE1}}"
                                },
                                {
                                    "name": "{{FILE2}}"
                                }
                            ]
                        }
                    ]
                },
                "result": "success",
                "tag": 3
            }
            """;
            response = MockUtils.PutPathInResponse(response, "{{DIR}}", FileUtils.GetAbsolutePath("downloads"));
            response = MockUtils.PutPathInResponse(response, "{{FILE1}}", Path.Join(" Book Folder ", "chapter1.m4b"));
            response = MockUtils.PutPathInResponse(response, "{{FILE2}}", Path.Join(" Book Folder ", "book.txt"));
            return MockUtils.GetCannedResponse(response);
        }
    }
}
