using System.Collections.Specialized;
using System.Net;
using System.Web;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class QbittorrentApiMock : BaseApiMock
    {
        public bool Authenticated { get; set; } = false;

        public QbittorrentApiMock()
        {
            AddRoute("api/v2/auth/login", DoLogin, HttpMethod.Post);
            AddRoute("api/v2/torrents/add", DoAdd, HttpMethod.Post);
            AddRoute("api/v2/app/version", GetVersion, HttpMethod.Get);
            AddRoute("api/v2/torrents/info", GetInfo, HttpMethod.Get);
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

            return MockUtils.GetCannedResponse("Ok");
        }

        private async Task<HttpResponseMessage> GetInfo(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return MockUtils.GetCannedResponse("""
            [
                {
                    "hash": "NEWHASH",
                    "name": "Book"
                }
            ]
            """);
        }

        private async Task<HttpResponseMessage> GetVersion(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Authenticated)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return MockUtils.GetCannedResponse("v5.0.2");
        }
    }
}
