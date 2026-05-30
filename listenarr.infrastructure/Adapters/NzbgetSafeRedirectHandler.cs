/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    /// <summary>
    /// Thrown by <see cref="NzbgetSafeRedirectHandler"/> when a redirect is refused (cross-host,
    /// HTTPS->HTTP downgrade, missing Location, or loop). Carries a descriptive message that
    /// the adapter surfaces to the user instead of the generic "network error" fallback.
    /// </summary>
    public sealed class NzbgetSafeRedirectException : HttpRequestException
    {
        public NzbgetSafeRedirectException(string message) : base(message) { }
    }

    /// <summary>
    /// Manual redirect handler for the NZBGet HttpClient.
    ///
    /// The named "nzbget" HttpClient is configured with <c>AllowAutoRedirect = false</c> because
    /// .NET's default redirect-follower strips <c>Authorization</c> headers across hops to prevent
    /// credential leakage to unintended hosts. That protection breaks NZBGet setups where a reverse
    /// proxy in front of NZBGet issues a 30x (typical cases: a Caddy/Nginx HTTPS upgrade, or
    /// trailing-slash normalization on a configured base URL) — the redirect succeeds but the
    /// subsequent request hits NZBGet without auth and returns 401 Unauthorized.
    ///
    /// This handler re-applies the <c>Authorization</c> header on a redirect, but only when both
    /// safety rules pass:
    ///   1. The redirect target is the same host:port as the original request.
    ///   2. The redirect does not downgrade from HTTPS to HTTP.
    ///
    /// On any rule violation the handler throws a descriptive <see cref="HttpRequestException"/>
    /// instead of silently dropping auth, so misconfigured proxies surface as actionable errors
    /// rather than mysterious "Unauthorized" failures.
    /// </summary>
    public sealed class NzbgetSafeRedirectHandler : DelegatingHandler
    {
        internal const int MaxRedirects = 5;

        private readonly ILogger<NzbgetSafeRedirectHandler>? _logger;

        public NzbgetSafeRedirectHandler()
        {
        }

        public NzbgetSafeRedirectHandler(ILogger<NzbgetSafeRedirectHandler> logger)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer request content once so we can replay POST bodies across 307/308 redirects.
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            }

            var currentRequest = request;
            HttpRequestMessage? clonedRequest = null;
            var hops = 0;

            try
            {
                while (true)
                {
                    var response = await base.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);

                    if (!IsRedirectStatus(response.StatusCode))
                    {
                        return response;
                    }

                    if (hops >= MaxRedirects)
                    {
                        var sanitized = SanitizeUri(currentRequest.RequestUri);
                        response.Dispose();
                        throw new NzbgetSafeRedirectException(
                            $"NZBGet request to {sanitized} hit the redirect cap of {MaxRedirects} hops. " +
                            "This likely indicates a redirect loop in your reverse-proxy or NZBGet base-URL configuration.");
                    }

                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        var sanitized = SanitizeUri(currentRequest.RequestUri);
                        var status = response.StatusCode;
                        response.Dispose();
                        throw new NzbgetSafeRedirectException(
                            $"NZBGet responded {(int)status} {status} to {sanitized} but did not include a Location header. " +
                            "Cannot follow the redirect; check the NZBGet (or proxy) server logs.");
                    }

                    var nextUri = location.IsAbsoluteUri
                        ? location
                        : new Uri(currentRequest.RequestUri!, location);

                    if (!IsSameOriginHost(currentRequest.RequestUri!, nextUri))
                    {
                        var fromUri = SanitizeUri(currentRequest.RequestUri);
                        var toUri = SanitizeUri(nextUri);
                        response.Dispose();
                        throw new NzbgetSafeRedirectException(
                            $"NZBGet redirected from {fromUri} to a different host ({toUri}). " +
                            "Authorization credentials would be forwarded to an unexpected host, so the redirect was blocked. " +
                            "Fix the NZBGet base URL or reverse-proxy rewrite so the response is returned directly.");
                    }

                    if (IsSchemeDowngrade(currentRequest.RequestUri!, nextUri))
                    {
                        var fromUri = SanitizeUri(currentRequest.RequestUri);
                        var toUri = SanitizeUri(nextUri);
                        response.Dispose();
                        throw new NzbgetSafeRedirectException(
                            $"NZBGet redirected from HTTPS ({fromUri}) to HTTP ({toUri}). " +
                            "Credentials would be sent in clear, so the redirect was blocked. " +
                            "Configure NZBGet (or its reverse proxy) to serve only HTTPS.");
                    }

                    var nextRequest = BuildRedirectedRequest(currentRequest, response.StatusCode, nextUri);
                    _logger?.LogDebug(
                        "NZBGet redirect {Status} {From} -> {To} (hop {Hop}/{Cap})",
                        (int)response.StatusCode,
                        SanitizeUri(currentRequest.RequestUri),
                        SanitizeUri(nextUri),
                        hops + 1,
                        MaxRedirects);

                    response.Dispose();
                    if (clonedRequest != null)
                    {
                        clonedRequest.Dispose();
                    }
                    clonedRequest = nextRequest;
                    currentRequest = nextRequest;
                    hops++;
                }
            }
            catch
            {
                clonedRequest?.Dispose();
                throw;
            }
        }

        private static HttpRequestMessage BuildRedirectedRequest(
            HttpRequestMessage previous,
            HttpStatusCode redirectStatus,
            Uri nextUri)
        {
            // RFC 7231 §6.4: 301/302/303 may change the method to GET (303 must); 307/308 preserve method+body.
            // Matches HttpClientHandler's default behavior.
            bool preserveMethodAndBody =
                redirectStatus == HttpStatusCode.TemporaryRedirect ||
                redirectStatus == HttpStatusCode.PermanentRedirect;

            var nextMethod = preserveMethodAndBody ? previous.Method : HttpMethod.Get;
            var nextContent = preserveMethodAndBody ? previous.Content : null;

            var next = new HttpRequestMessage(nextMethod, nextUri)
            {
                Version = previous.Version,
                VersionPolicy = previous.VersionPolicy,
            };

            if (nextContent != null)
            {
                next.Content = nextContent;
            }

            // Re-apply the Authorization header — this handler's whole reason for existing.
            if (previous.Headers.Authorization != null)
            {
                next.Headers.Authorization = previous.Headers.Authorization;
            }

            // Carry over the rest of the request headers (Accept, User-Agent, etc.), skipping
            // Authorization (already copied above) and Host (must derive from the new URI).
            foreach (var header in previous.Headers)
            {
                if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return next;
        }

        private static bool IsRedirectStatus(HttpStatusCode code) => code switch
        {
            HttpStatusCode.MovedPermanently => true,   // 301
            HttpStatusCode.Found => true,              // 302
            HttpStatusCode.SeeOther => true,           // 303
            HttpStatusCode.TemporaryRedirect => true,  // 307
            HttpStatusCode.PermanentRedirect => true,  // 308
            _ => false
        };

        private static bool IsSameOriginHost(Uri from, Uri to) =>
            string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase) &&
            from.Port == to.Port;

        private static bool IsSchemeDowngrade(Uri from, Uri to) =>
            string.Equals(from.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(to.Scheme, "http", StringComparison.OrdinalIgnoreCase);

        private static string SanitizeUri(Uri? uri)
        {
            if (uri == null) return "(null)";
            if (string.IsNullOrEmpty(uri.UserInfo)) return uri.ToString();
            var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            return builder.Uri.ToString();
        }
    }
}
