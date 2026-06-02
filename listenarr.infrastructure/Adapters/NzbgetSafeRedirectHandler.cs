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
using Listenarr.Application.Security;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
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
    /// On any rule violation the handler throws a descriptive <see cref="NzbgetSafeRedirectException"/>
    /// instead of silently dropping auth, so misconfigured proxies surface as actionable errors
    /// rather than mysterious "Unauthorized" failures.
    /// </summary>
    public sealed class NzbgetSafeRedirectHandler(ILogger<NzbgetSafeRedirectHandler> logger) : DelegatingHandler
    {
        internal const int MaxRedirects = 5;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer request content once so we can replay POST bodies across 307/308 redirects.
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            }

            var currentRequest = request;
            HttpRequestMessage? clonedRequest = null;

            try
            {
                // hops in [0, MaxRedirects] => up to MaxRedirects + 1 requests sent. The original
                // request plus MaxRedirects follows; falling out of the loop means every allowed
                // hop was still a redirect, i.e. a loop or misconfiguration.
                for (var hops = 0; hops <= MaxRedirects; hops++)
                {
                    var response = await base.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);

                    if (!OutboundRequestSecurity.IsRedirectStatusCode(response.StatusCode))
                    {
                        // Terminal response — hand it back to the caller, who owns its disposal.
                        return response;
                    }

                    // A redirect we won't return: scope its disposal to this iteration.
                    using (response)
                    {
                        var location = response.Headers.Location;
                        if (location == null)
                        {
                            var sanitized = LogRedaction.SanitizeUrl(currentRequest.RequestUri?.ToString());
                            throw new NzbgetSafeRedirectException(
                                $"NZBGet responded {(int)response.StatusCode} {response.StatusCode} to {sanitized} but did not include a Location header. " +
                                "Cannot follow the redirect; check the NZBGet (or proxy) server logs.");
                        }

                        var nextUri = location.IsAbsoluteUri
                            ? location
                            : new Uri(currentRequest.RequestUri!, location);

                        if (!OutboundRequestSecurity.IsSameHostAndPort(currentRequest.RequestUri!, nextUri))
                        {
                            var fromUri = LogRedaction.SanitizeUrl(currentRequest.RequestUri?.ToString());
                            var toUri = LogRedaction.SanitizeUrl(nextUri.ToString());
                            throw new NzbgetSafeRedirectException(
                                $"NZBGet redirected from {fromUri} to a different host ({toUri}). " +
                                "Authorization credentials would be forwarded to an unexpected host, so the redirect was blocked. " +
                                "Fix the NZBGet base URL or reverse-proxy rewrite so the response is returned directly.");
                        }

                        if (OutboundRequestSecurity.IsHttpsToHttpDowngrade(currentRequest.RequestUri!, nextUri))
                        {
                            var fromUri = LogRedaction.SanitizeUrl(currentRequest.RequestUri?.ToString());
                            var toUri = LogRedaction.SanitizeUrl(nextUri.ToString());
                            throw new NzbgetSafeRedirectException(
                                $"NZBGet redirected from HTTPS ({fromUri}) to HTTP ({toUri}). " +
                                "Credentials would be sent in clear, so the redirect was blocked. " +
                                "Configure NZBGet (or its reverse proxy) to serve only HTTPS.");
                        }

                        var nextRequest = BuildRedirectedRequest(currentRequest, response.StatusCode, nextUri);
                        logger.LogDebug(
                            "NZBGet redirect {Status} {From} -> {To} (hop {Hop}/{Cap})",
                            (int)response.StatusCode,
                            LogRedaction.SanitizeUrl(currentRequest.RequestUri?.ToString()),
                            LogRedaction.SanitizeUrl(nextUri.ToString()),
                            hops + 1,
                            MaxRedirects);

                        clonedRequest?.Dispose();
                        clonedRequest = nextRequest;
                        currentRequest = nextRequest;
                    }
                }

                var sanitizedLast = LogRedaction.SanitizeUrl(currentRequest.RequestUri?.ToString());
                throw new NzbgetSafeRedirectException(
                    $"NZBGet request to {sanitizedLast} hit the redirect cap of {MaxRedirects} hops. " +
                    "This likely indicates a redirect loop in your reverse-proxy or NZBGet base-URL configuration.");
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
    }
}
