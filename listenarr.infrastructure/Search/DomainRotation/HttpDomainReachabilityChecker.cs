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

namespace Listenarr.Infrastructure.Search.DomainRotation
{
    public class HttpDomainReachabilityChecker(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpDomainReachabilityChecker> logger) : IDomainReachabilityChecker
    {
        // ponytail: 8s timeout hardcoded — add a settings field if users need to tune it
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

        public async Task<bool> IsReachableAsync(string scheme, string host, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ProbeTimeout);

                var client = httpClientFactory.CreateClient("DomainReachability");
                var url = $"{scheme}://{host}/";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // browser-like UA avoids trivial blocks on user-agent
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/125.0.0.0 Safari/537.36");

                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // any HTTP response (including 403 Cloudflare) = server is alive = reachable
                // ponytail: treat-403-as-reachable — 403 from Cloudflare means the domain resolves
                // and a server is listening; only DNS/timeout/refused counts as unreachable
                logger.LogDebug("Reachability probe {Url}: {StatusCode}", url, (int)response.StatusCode);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // propagate external cancellation; don't swallow it as "unreachable"
            }
            catch (Exception ex)
            {
                // DNS failure, inner timeout, connection refused, SSL error → unreachable
                logger.LogDebug(ex, "Reachability probe {Scheme}://{Host}/ failed: {Message}",
                    scheme, host, ex.Message);
                return false;
            }
        }
    }
}
