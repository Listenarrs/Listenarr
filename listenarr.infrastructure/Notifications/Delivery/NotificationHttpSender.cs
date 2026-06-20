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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Notifications.Delivery
{
    internal sealed class NotificationHttpSender(
        HttpClient httpClient,
        HttpClient httpClientNoRedirect,
        ILogger logger,
        Func<bool> allowPrivateTargetsForCurrentRequest)
    {
        public async Task<HttpResponseMessage> PostValidatedAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            return await SendValidatedAsync(request, cancellationToken);
        }

        public async Task<HttpResponseMessage> SendValidatedAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            if (request.RequestUri == null)
            {
                throw new InvalidOperationException("Outbound notification request URI is required.");
            }

            var allowPrivateTargets = allowPrivateTargetsForCurrentRequest();
            if (!OutboundRequestSecurity.TryValidateExternalHttpUri(request.RequestUri, out var uriReason, allowPrivateTargets))
            {
                throw new InvalidOperationException($"Blocked outbound URL: {uriReason}");
            }

            if (!await OutboundRequestSecurity.TryValidateResolvedExternalHttpUriAsync(request.RequestUri, logger, allowPrivateTargets))
            {
                throw new InvalidOperationException("Blocked outbound URL: DNS resolved to private or loopback address");
            }

            if (ReferenceEquals(httpClientNoRedirect, httpClient))
            {
                var directResponse = await httpClient.SendAsync(request, cancellationToken);
                var finalUri = directResponse.RequestMessage?.RequestUri ?? request.RequestUri;
                if (!OutboundRequestSecurity.TryValidateExternalHttpUri(finalUri, out var finalReason, allowPrivateTargets))
                {
                    directResponse.Dispose();
                    throw new InvalidOperationException($"Blocked final outbound URL: {finalReason}");
                }

                if (!await OutboundRequestSecurity.TryValidateResolvedExternalHttpUriAsync(finalUri, logger, allowPrivateTargets))
                {
                    directResponse.Dispose();
                    throw new InvalidOperationException("Blocked final outbound URL: DNS resolved to private or loopback address");
                }

                return directResponse;
            }

            var bufferedContent = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : null;
            var contentHeaderSnapshot = request.Content?.Headers
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray()))
                .ToList();
            var requestHeaderSnapshot = request.Headers
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray()))
                .ToList();
            var method = request.Method;
            var version = request.Version;
            var versionPolicy = request.VersionPolicy;

            var (response, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
                currentUri =>
                {
                    var retryRequest = new HttpRequestMessage(method, currentUri)
                    {
                        Version = version,
                        VersionPolicy = versionPolicy
                    };

                    foreach (var header in requestHeaderSnapshot)
                    {
                        retryRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (bufferedContent != null)
                    {
                        var retryContent = new ByteArrayContent(bufferedContent);
                        if (contentHeaderSnapshot != null)
                        {
                            foreach (var header in contentHeaderSnapshot)
                            {
                                retryContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }

                        retryRequest.Content = retryContent;
                    }

                    return retryRequest;
                },
                request.RequestUri,
                httpClientNoRedirect,
                logger,
                allowPrivateTargets: allowPrivateTargets,
                cancellationToken: cancellationToken);

            return response;
        }
    }
}
