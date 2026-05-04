using System.Web;
using Listenarr.Domain.Utils;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class SabnzbdApiMock : BaseApiMock
    {
        public static readonly string SINGLE_FILE_SABNZBD = "SABnzbd_nzo_1";
        public static readonly string MULTI_FILE_SABNZBD = "SABnzbd_nzo_2";
        public static readonly string REMOTE_PATH = FileUtils.GetAbsolutePath("downloads", "completed");

        public SabnzbdApiMock()
        {
            AddRoute("api", GetHistory, HttpMethod.Get);
        }

        public static async Task<HttpResponseMessage> GetHistory(HttpRequestMessage request, CancellationToken ct)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var mode = query["mode"] ?? "";

            if (string.Equals("history", mode))
            {
                var response = """
                {
                    "history" : {
                        "slots": [
                            {
                                "nzo_id": "{{SINGLE_FILE_SABNZBD}}",
                                "storage": "/completed/Book.m4b"
                            },
                            {
                                "nzo_id": "{{MULTI_FILE_SABNZBD}}",
                                "storage": "/completed/Book Folder"
                            }
                        ]
                    }
                }
                """;
                response = response.Replace("{{SINGLE_FILE_SABNZBD}}", SINGLE_FILE_SABNZBD);
                response = response.Replace("{{MULTI_FILE_SABNZBD}}", MULTI_FILE_SABNZBD);
                return MockUtils.GetCannedResponse(response);
            }

            else if (string.Equals("queue", mode))
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
                var remote_path = REMOTE_PATH.Replace("\\", "\\\\");
                response = response.Replace("{{REMOTE_PATH}}", remote_path);
                return MockUtils.GetCannedResponse(response);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }
}
