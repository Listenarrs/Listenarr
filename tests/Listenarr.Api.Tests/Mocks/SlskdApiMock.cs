using System.Net;
using System.Text.Json;
using Listenarr.Api.Tests;

public class SlskdApiMock : BaseMock
{
    public SlskdApiMock()
    {
        // Searches
        AddRoute("api/v0/searches", PostSearchAsync, HttpMethod.Post);
        AddRoute("api/v0/searches/[^/]+$", GetSearchStateAsync, HttpMethod.Get);
        AddRoute("api/v0/searches/[^/]+/responses", GetSearchResultAsync, HttpMethod.Get);

        // Transfers
        AddRoute("api/v0/transfers/downloads", PostDownloadAsync, HttpMethod.Post);
        AddRoute("api/v0/transfers/downloads", DeleteDownloadAsync, HttpMethod.Delete);
        AddRoute("api/v0/transfers/downloads", GetDownloadsAsync, HttpMethod.Get);

        // Options
        AddRoute("api/v0/options", GetOptions, HttpMethod.Get);
    }

    public async Task<HttpResponseMessage> PostSearchAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return MockUtils.GetCannedResponse("""
        {
            "fileCount": 0,
            "id": "370a4598-5287-47a5-b613-8cb7c5a5d98d",
            "isComplete": false,
            "lockedFileCount": 0,
            "responseCount": 0,
            "responses": [],
            "searchText": "TEST",
            "startedAt": "2026-04-07T10:54:56.5866789Z",
            "state": "InProgress",
            "token": 153821
        }
        """);
    }

    public async Task<HttpResponseMessage> GetSearchStateAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return MockUtils.GetCannedResponse("""
        {
            "endedAt": "2026-04-07T10:54:57.4959509Z",
            "fileCount": 1759,
            "id": "370a4598-5287-47a5-b613-8cb7c5a5d98d",
            "isComplete": true,
            "lockedFileCount": 291,
            "responseCount": 103,
            "responses": [],
            "searchText": "TEST",
            "startedAt": "2026-04-07T10:54:56.5866789Z",
            "state": "Completed, ResponseLimitReached",
            "token": 153821
        }
        """);
    }

    public async Task<HttpResponseMessage> GetSearchResultAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return MockUtils.GetCannedResponse("""
        [
            {
                "fileCount": 5,
                "files": [
                    {
                        "code": 1,
                        "extension": "",
                        "filename": "Certificate 18 CERT1804 - Studio Pressure (1993)\\cert1804 - B1 - Studio Pressure - Test 1.mp3",
                        "size": 6340608,
                        "isLocked": false
                    },
                    {
                        "code": 1,
                        "extension": "",
                        "filename": "Certificate 18 CERT1804 - Studio Pressure (1993)\\cert1804 - B1 - Studio Pressure - Test 2.mp3",
                        "size": 8443982,
                        "isLocked": false
                    },
                    {
                        "code": 1,
                        "extension": "",
                        "filename": "Fear Factory\\1992 - Fear Factory - Soul Of A New Machine [EU Vinyl LP 24-192 kHz]\\03. Crash Test 1.flac",
                        "size": 36787344,
                        "isLocked": false
                    },
                    {
                        "bitDepth": 24,
                        "code": 1,
                        "extension": "",
                        "filename": "Fear Factory\\1992 - Fear Factory - Soul Of A New Machine [EU Vinyl LP 24-192 kHz]\\05. Crash Test 2.flac",
                        "length": 227,
                        "sampleRate": 192000,
                        "size": 166714323,
                        "isLocked": false
                    },
                    {
                        "bitDepth": 16,
                        "code": 1,
                        "extension": "",
                        "filename": "Fear Factory\\1999 - Fear Factory - Messiah [Russia]\\01. Crash Test.flac",
                        "length": 228,
                        "sampleRate": 44100,
                        "size": 29736383,
                        "isLocked": false
                    }
                ],
                "hasFreeUploadSlot": false,
                "lockedFileCount": 0,
                "lockedFiles": [],
                "queueLength": 54,
                "token": 153821,
                "uploadSpeed": 1260583,
                "username": "USER1"
            },
            {
                "fileCount": 6,
                "files": [
                    {
                        "bitRate": 320,
                        "code": 1,
                        "extension": "",
                        "filename": "House\\House - Classics -\\1995\\Distant Drums - Acid Test.mp3",
                        "length": 375,
                        "size": 15028874,
                        "isLocked": false
                    },
                    {
                        "bitRate": 256,
                        "code": 1,
                        "extension": "",
                        "filename": "House\\House - Classics -\\1995\\Distant Piano - Acid Test.mp3",
                        "length": 459,
                        "size": 14710808,
                        "isLocked": false
                    },
                    {
                        "bitRate": 256,
                        "code": 1,
                        "extension": "",
                        "filename": "S-Range - Space  (2003)-\\04. SRange - Test Tones.mp3",
                        "length": 489,
                        "size": 15676150,
                        "isLocked": false
                    }
                ],
                "hasFreeUploadSlot": true,
                "lockedFileCount": 0,
                "lockedFiles": [],
                "queueLength": 133,
                "token": 153821,
                "uploadSpeed": 1878836,
                "username": "USER2"
            }
        ]
        """);
    }

    public async Task<HttpResponseMessage> PostDownloadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.GetArrayLength() > 0)
        {
            return MockUtils.GetCannedResponse("""
            {
                "enqueued": [
                    {
                        "id": "241d6a7a-b15f-4ebd-a2bb-09504a8f8d32",
                        "username": "novatune",
                        "direction": "Download",
                        "filename": "@@zxunv\\m.books.texts\\a-z by author [individual]\\I\\Isaac Asimov\\14 The Foundation Saga - Isaac Asimov\\Forward the Foundation - Isaac Asimov.epub",
                        "size": 546389,
                        "startOffset": 0,
                        "state": "Queued, Locally",
                        "stateDescription": "Queued, Locally",
                        "requestedAt": "2026-04-06T18:46:42.1252181Z",
                        "bytesTransferred": 0,
                        "averageSpeed": 0,
                        "bytesRemaining": 546389,
                        "percentComplete": 0
                    }
                ],
                "failed": []
            }
            """);
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    public async Task<HttpResponseMessage> DeleteDownloadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    public async Task<HttpResponseMessage> GetDownloadsAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return MockUtils.GetCannedResponse("""
        [
            {
                "username": "USER2",
                "directories": [
                    {
                        "directory": "ZZZZ\\YYYY\\TEST_SUCCESS",
                        "fileCount": 1,
                        "files": [
                            {
                                "id": "USER2_BOOK1_FILE1",
                                "username": "USER2",
                                "direction": "Download",
                                "filename": "ZZZZ\\YYYY\\FILE00001.mp3",
                                "size": 1337,
                                "startOffset": 0,
                                "state": "Completed, Succeeded",
                                "stateDescription": "Completed, Succeeded",
                                "requestedAt": "2026-04-06T18:46:42.1252181",
                                "enqueuedAt": "2026-04-06T18:46:42.6491911",
                                "startedAt": "2026-04-06T18:46:44.3246205Z",
                                "endedAt": "2026-04-06T18:46:46.1116543Z",
                                "bytesTransferred": 1337,
                                "averageSpeed": 305751.91135164874,
                                "bytesRemaining": 0,
                                "elapsedTime": "00:00:01.7870338",
                                "percentComplete": 100,
                                "remainingTime": "00:00:00"
                            }
                        ]
                    }
                ]
            },
            {
                "username": "USER1",
                "directories": [
                    {
                        "directory": "BOOKDIRECTORY",
                        "fileCount": 1,
                        "files": [
                            {
                                "id": "241d6a7a-b15f-4ebd-a2bb-09504a8f8d32",
                                "username": "USER1",
                                "direction": "Download",
                                "filename": "BOOKDIRECTORY\\Forward the Foundation - Isaac Asimov.epub",
                                "size": 546389,
                                "startOffset": 0,
                                "state": "Completed, Succeeded",
                                "stateDescription": "Completed, Succeeded",
                                "requestedAt": "2026-04-06T18:46:42.1252181",
                                "enqueuedAt": "2026-04-06T18:46:42.6491911",
                                "startedAt": "2026-04-06T18:46:44.3246205Z",
                                "endedAt": "2026-04-06T18:46:46.1116543Z",
                                "bytesTransferred": 546389,
                                "averageSpeed": 305751.91135164874,
                                "bytesRemaining": 0,
                                "elapsedTime": "00:00:01.7870338",
                                "percentComplete": 100,
                                "remainingTime": "00:00:00"
                            }
                        ]
                    },
                    {
                        "directory": "AAAA\\BBBB\\Awesome Book",
                        "fileCount": 2,
                        "files": [
                            {
                                "id": "USER1_BOOK2_FILE1",
                                "username": "USER1",
                                "direction": "Download",
                                "filename": "AAAA\\BBBB\\Awesome Book\\FILE_1.mp3",
                                "size": 10,
                                "startOffset": 0,
                                "state": "InProgress",
                                "stateDescription": "InProgress",
                                "requestedAt": "2026-04-06T18:46:42.1252181",
                                "enqueuedAt": "2026-04-06T18:46:42.6491911",
                                "startedAt": "2026-04-06T18:46:44.3246205Z",
                                "bytesTransferred": 5,
                                "averageSpeed": 305751.91135164874,
                                "bytesRemaining": 5,
                                "elapsedTime": "00:00:01.7870338",
                                "percentComplete": 50
                            },
                            {
                                "id": "USER1_BOOK2_FILE2",
                                "username": "USER1",
                                "direction": "Download",
                                "filename": "AAAA\\BBBB\\Awesome Book\\FILE_2.mp3",
                                "size": 20,
                                "startOffset": 0,
                                "state": "Queued, Remotely",
                                "stateDescription": "Queued, Remotely",
                                "requestedAt": "2026-04-06T18:46:42.1252181",
                                "enqueuedAt": "2026-04-06T18:46:42.6491911",
                                "bytesTransferred": 0,
                                "averageSpeed": 305751.91135164874,
                                "bytesRemaining": 20,
                                "percentComplete": 0,
                                "remainingTime": "00:00:00"
                            }
                        ]
                    }
                ]
            }
        ]
        """);
    }

    public async Task<HttpResponseMessage> GetOptions(HttpRequestMessage request, CancellationToken ct)
    {
        return MockUtils.GetCannedResponse("""
        {
            "debug": false,
            "headless": false,
            "remoteConfiguration": false,
            "remoteFileManagement": false,
            "instanceName": "default",
            "flags": {
                "noLogo": false,
                "noStart": false,
                "noConfigWatch": false,
                "noConnect": false,
                "noShareScan": false,
                "forceShareScan": false,
                "forceMigrations": false,
                "noVersionCheck": false,
                "logSQL": false,
                "logUnobservedExceptions": false,
                "experimental": false,
                "volatile": false,
                "caseSensitiveRegEx": false,
                "legacyWindowsTcpKeepalive": false,
                "optimisticRelayFileInfo": false,
                "noSqlitePooling": false
            },
            "relay": {
                "enabled": false,
                "mode": "controller",
                "controller": {
                    "ignoreCertificateErrors": false,
                    "downloads": false
                },
                "agents": {}
            },
            "permissions": {
                "file": {}
            },
            "directories": {
                "incomplete": "/data/incomplete",
                "downloads": "/data/complete"
            },
            "shares": {
                "directories": [
                    "/music/drive1",
                    "/music/drive2"
                ],
                "filters": [],
                "cache": {
                    "storageMode": "memory",
                    "workers": 4
                }
            },
            "global": {
                "upload": {
                    "slots": 10,
                    "speedLimit": 2147483647
                },
                "limits": {
                    "queued": {},
                    "daily": {},
                    "weekly": {}
                },
                "download": {
                    "slots": 2147483647,
                    "speedLimit": 2147483647
                }
            }
        }
        """);
    }
}