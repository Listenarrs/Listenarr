using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public sealed record MyAnonamouseMockRoute(
        string Pattern,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler)
    {
        public HttpMethod? Method { get; init; }
    }

    public class MyAnonamouseApiMock : BaseApiMock
    {
        private static readonly HttpRequestOptionsKey<bool> MatchedRouteKey = new("MyAnonamouseMatchedRoute");
        private readonly object routeInvocationLock = new();
        private readonly Dictionary<string, int> routeInvocationCounts = new(StringComparer.Ordinal);
        private readonly string cookieValue = "new_mam";
        private bool defaultRoutesRegistered;

        public bool FailOnUnexpectedCalls { get; set; }

        public void AddTrackedRoute(string routeName, MyAnonamouseMockRoute route)
        {
            lock (routeInvocationLock)
            {
                routeInvocationCounts.TryAdd(routeName, 0);
            }

            AddRoute(
                route.Pattern,
                async (request, cancellationToken) =>
                {
                    IncrementRouteInvocationCount(routeName);
                    request.Options.Set(MatchedRouteKey, true);
                    return await route.Handler(request, cancellationToken);
                },
                route.Method);
        }

        public int GetRouteInvocationCount(string routeName)
        {
            lock (routeInvocationLock)
            {
                return routeInvocationCounts.GetValueOrDefault(routeName);
            }
        }

        public void ResetObservations()
        {
            ResetCallCount();
            ResetRequestHistory();

            lock (routeInvocationLock)
            {
                foreach (var routeName in routeInvocationCounts.Keys.ToArray())
                {
                    routeInvocationCounts[routeName] = 0;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            EnsureDefaultRoutesRegistered();
            var response = await base.SendAsync(request, cancellationToken);
            if (!FailOnUnexpectedCalls || request.Options.TryGetValue(MatchedRouteKey, out _))
            {
                return response;
            }

            response.Dispose();
            throw new InvalidOperationException(
                $"Unexpected MyAnonamouse API request: {request.Method} {request.RequestUri}");
        }

        public HttpResponseMessage AddCookies(HttpResponseMessage response, string value)
        {
            response.Headers.Add("Set-Cookie", "mam_id=\"" + value + "\"; Path=/; HttpOnly");
            return response;
        }

        public async Task<HttpResponseMessage> GetSearch(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return AddCookies(MockUtils.GetCannedResponse("""{"data":[]}"""), cookieValue);
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
            if (GetRouteInvocationCount("redirect-start") == 1)
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

        private void IncrementRouteInvocationCount(string routeName)
        {
            lock (routeInvocationLock)
            {
                routeInvocationCounts[routeName]++;
            }
        }

        private void EnsureDefaultRoutesRegistered()
        {
            lock (routeInvocationLock)
            {
                if (defaultRoutesRegistered)
                {
                    return;
                }

                AddTrackedRoute(
                    "search",
                    new MyAnonamouseMockRoute("/tor/js/loadSearchJSONbasic.php", GetSearch)
                    {
                        Method = HttpMethod.Get
                    });
                AddTrackedRoute(
                    "error-download",
                    new MyAnonamouseMockRoute("/tor/download.php/me", GetError)
                    {
                        Method = HttpMethod.Get
                    });
                AddTrackedRoute(
                    "dummy-download",
                    new MyAnonamouseMockRoute(@"/tor/download\.php/dummy", GetDummyDownload)
                    {
                        Method = HttpMethod.Get
                    });
                AddTrackedRoute(
                    "download",
                    new MyAnonamouseMockRoute(@"/tor/download\.php(?!/me)(?!/dummy)", GetDownload)
                    {
                        Method = HttpMethod.Get
                    });
                AddTrackedRoute(
                    "redirect-start",
                    new MyAnonamouseMockRoute("/tor/redirectstart", GetRedirectedDownload)
                    {
                        Method = HttpMethod.Get
                    });
                defaultRoutesRegistered = true;
            }
        }
    }
}
