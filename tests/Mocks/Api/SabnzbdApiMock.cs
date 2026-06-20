using System.Web;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class SabnzbdApiMock : BaseApiMock
    {
        public static readonly string SINGLE_FILE_SABNZBD = "SABnzbd_nzo_1";
        public static readonly string MULTI_FILE_SABNZBD = "SABnzbd_nzo_2";
        public static readonly string COMPLETED_FILE_SABNZBD = "SABnzbd_nzo_3";
        public static readonly string REMOTE_PATH = FileUtils.GetAbsolutePath("downloads", "completed");

        public string contentPath = FileUtils.GetAbsolutePath("completed", "Book.m4b");
        public List<Uri> RemovalRequests { get; } = [];

        public SabnzbdApiMock()
        {
            AddRoute("api", ProcessRequest, HttpMethod.Get);
        }

        public async Task<HttpResponseMessage> GetHistory(HttpRequestMessage request, CancellationToken ct)
        {
            var response = """
            {
                "history" : {
                    "slots": [
                        {
                            "nzo_id": "{{SINGLE_FILE_SABNZBD}}",
                            "storage": "{{ARBITRARY_PATH_1}}"
                        },
                        {
                            "name": "hello",
                            "nzo_id": "{{COMPELTED_FILE_SABNZBD}}",
                            "status": "Completed",
                            "storage": "{{CONTENT_PATH}}"
                        },
                        {
                            "nzo_id": "{{MULTI_FILE_SABNZBD}}",
                            "storage": "{{ARBITRARY_PATH_2}}"
                        }
                    ]
                }
            }
            """;
            response = response.Replace("{{SINGLE_FILE_SABNZBD}}", SINGLE_FILE_SABNZBD);
            response = response.Replace("{{MULTI_FILE_SABNZBD}}", MULTI_FILE_SABNZBD);
            response = response.Replace("{{COMPELTED_FILE_SABNZBD}}", COMPLETED_FILE_SABNZBD);
            response = MockUtils.PutPathInResponse(response, "{{ARBITRARY_PATH_1}}", FileUtils.GetAbsolutePath("completed", "Book.m4b"));
            response = MockUtils.PutPathInResponse(response, "{{ARBITRARY_PATH_2}}", FileUtils.GetAbsolutePath("completed", "Book Folder"));
            response = MockUtils.PutPathInResponse(response, "{{CONTENT_PATH}}", contentPath);
            return MockUtils.GetCannedResponse(response);
        }

        public async Task<HttpResponseMessage> GetQueue(HttpRequestMessage request, CancellationToken ct)
        {
            var response = """
            {
                "queue": {
                    "slots": [
                        {
                            "nzo_id": "SABnzbd_nzo_20f9svw_",
                            "filename": "William Faulkner - The Sound and the Fury",
                            "percentage": "50.5",
                            "mb": "100.0",
                            "mbleft": "49.5",
                            "status": "Downloading"
                        },
                        {
                            "nzo_id": "SABnzbd_nzo_9plcy_gj",
                            "name": "William Faulkner - The Sound and the Fury",
                            "status": "Completed",
                            "storage": "{{REMOTE_PATH}}",
                            "completed": 1600000000
                        }
                    ]
                }
            }
            """;
            response = MockUtils.PutPathInResponse(response, "{{REMOTE_PATH}}", FileUtils.GetAbsolutePath("downloads", "completed"));
            return MockUtils.GetCannedResponse(response);
        }

        public async Task<HttpResponseMessage> GetVersion(HttpRequestMessage request, CancellationToken ct)
        {
            return MockUtils.GetCannedResponse("""
            {
                "version": "4.4.1"
            }
            """);
        }

        public async Task<HttpResponseMessage> ProcessRequest(HttpRequestMessage request, CancellationToken ct)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var mode = query["mode"] ?? "";

            if (string.Equals("version", mode))
            {
                return await GetVersion(request, ct);
            }
            else if (string.Equals("history", mode))
            {
                if (string.Equals("delete", query["name"], StringComparison.Ordinal))
                {
                    RemovalRequests.Add(request.RequestUri!);
                    return MockUtils.GetCannedResponse("""{"status": true}""");
                }
                return await GetHistory(request, ct);
            }

            else if (string.Equals("queue", mode))
            {
                if (string.Equals("delete", query["name"], StringComparison.Ordinal))
                {
                    RemovalRequests.Add(request.RequestUri!);
                    return MockUtils.GetCannedResponse("""{"status": true}""");
                }
                return await GetQueue(request, ct);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }
}
