using System.Collections.Specialized;
using System.Net;
using System.Web;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class QbittorrentApiMock : BaseApiMock
    {
        public bool Authenticated { get; set; } = false;
        public NameValueCollection? LastDeleteForm { get; private set; }
        public NameValueCollection? LastCategoryForm { get; private set; }
        public HttpStatusCode InfoStatusCode { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode AddStatusCode { get; set; } = HttpStatusCode.OK;
        public string? AddResponseBody { get; set; }
        public string? InfoResponseOverride { get; set; }

        public QbittorrentApiMock()
        {
            AddRoute("api/v2/auth/login", DoLogin, HttpMethod.Post);
            AddRoute("api/v2/torrents/add", DoAdd, HttpMethod.Post);
            AddRoute("api/v2/app/version", GetVersion, HttpMethod.Get);
            AddRoute("api/v2/torrents/info", GetInfo, HttpMethod.Get);
            AddRoute("api/v2/torrents/files", GetFiles, HttpMethod.Get);
            AddRoute("api/v2/torrents/delete", DoDelete, HttpMethod.Post);
            AddRoute("api/v2/torrents/setCategory", SetCategory, HttpMethod.Post);
        }

        private async Task<HttpResponseMessage> DoLogin(HttpRequestMessage request, CancellationToken ct)
        {
            string rawRequestBody = await request.Content.ReadAsStringAsync();
            NameValueCollection formData = HttpUtility.ParseQueryString(rawRequestBody);

            string username = formData["username"];
            string password = formData["password"];

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || !string.Equals(username, password))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            Authenticated = true;
            var response = MockUtils.GetCannedResponse("Ok");
            response.Headers.Add("Set-Cookie", "SID=1; HttpOnly; Path=/");

            return response;
        }

        private async Task<HttpResponseMessage> DoAdd(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (AddStatusCode != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(AddStatusCode)
                {
                    Content = new StringContent(AddResponseBody ?? string.Empty)
                };
            }

            return MockUtils.GetCannedResponse("Ok");
        }

        private async Task<HttpResponseMessage> GetInfo(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (InfoStatusCode != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(InfoStatusCode);
            }

            return MockUtils.GetCannedResponse(InfoResponseOverride ?? """
            [
                {
                    "hash": "NEWHASH",
                    "name": "Book",
                    "progress": 0.5,
                    "size": 1000,
                    "downloaded": 500,
                    "state": "downloading",
                    "save_path": "/downloads/book"
                }
            ]
            """);
        }

        private async Task<HttpResponseMessage> GetFiles(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return MockUtils.GetCannedResponse("[]");
        }

        private async Task<HttpResponseMessage> GetVersion(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return MockUtils.GetCannedResponse("v5.0.2");
        }

        private async Task<HttpResponseMessage> DoDelete(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            LastDeleteForm = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            return MockUtils.GetCannedResponse("Ok");
        }

        private async Task<HttpResponseMessage> SetCategory(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            LastCategoryForm = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            return MockUtils.GetCannedResponse("Ok");
        }
    }
}
