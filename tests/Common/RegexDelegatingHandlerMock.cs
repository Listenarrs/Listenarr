using System.Net;
using System.Text.RegularExpressions;

namespace Listenarr.Tests.Common
{
    /// <summary>
    /// Extends DelegatingHandlerMock to route requests to specific handlers based on URI Regex patterns.
    /// </summary>
    public class RegexDelegatingHandlerMock : DelegatingHandlerMock
    {
        private readonly List<(HttpMethod? Method, Regex, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)> _routes = new();

        public RegexDelegatingHandlerMock(Action<string>? log = null)
            : base((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)), log)
        {
        }

        /// <summary>
        /// Registers a regex pattern and the handler to execute if the URI matches
        /// </summary>
        public void AddRoute(string pattern, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, HttpMethod? method = null)
        {
            _routes.Add((method, new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase), handler));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            foreach (var (method, pattern, handler) in _routes)
            {
                bool methodMatches = method == null || method == request.Method;
                bool uriMatches = pattern.IsMatch(requestUri);

                if (methodMatches && uriMatches)
                {
                    return await handler(request, cancellationToken);
                }
            }

            // Fallback to the base handler (which returns 404 by default)
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
