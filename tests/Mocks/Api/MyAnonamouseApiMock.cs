using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class MyAnonamouseApiMock : BaseApiMock
    {
        private readonly string cookieValue = "new_mam";

        public MyAnonamouseApiMock()
        {
            AddRoute("/tor/js/loadSearchJSONbasic.php", GetSearch, HttpMethod.Get);
            AddRoute("/tor/download.php/me", GetError, HttpMethod.Get);
            AddRoute(@"/tor/download\.php/dummy", GetDummyDownload, HttpMethod.Get);
            AddRoute(@"/tor/download\.php(?!/me)(?!/dummy)", GetDownload, HttpMethod.Get);
            AddRoute("/tor/redirectstart", GetRedirectedDownload, HttpMethod.Get);
        }

        public HttpResponseMessage AddCookies(HttpResponseMessage response, string value)
        {
            response.Headers.Add("Set-Cookie", "mam_id=\"" + value + "\"; Path=/; HttpOnly");
            return response;
        }

        public async Task<HttpResponseMessage> GetSearch(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return AddCookies(MockUtils.GetCannedResponse("[]"), cookieValue);
        }

        public async Task<HttpResponseMessage> GetDummyDownload(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("dummy-torrent-bytes"));
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = "file.torrent"
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        }

        public async Task<HttpResponseMessage> GetDownload(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var stringContent = new StringBuilder()
                .Append("d")
                .Append("8:announce82:https://www.myanonamouse.net/tracker.php/mGDjyetAEBGCaneLZNS9OHawTo1upcwU/announce")
                .Append("e")
                .ToString();

            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(stringContent));
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = "file.torrent"
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        }

        public async Task<HttpResponseMessage> GetRedirectedDownload(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://47.39.239.96/tor/download.php/abc");
                return AddCookies(response, "redirect_mam");
            }

            return await GetDownload(request, cancellationToken);
        }

        public async Task<HttpResponseMessage> GetError(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return MockUtils.GetCannedResponse("""
            <html>
                <body>
                    Unrecognized host/PassKey
                </body>
            </html>
            """, "text/html");
        }
    }
}
