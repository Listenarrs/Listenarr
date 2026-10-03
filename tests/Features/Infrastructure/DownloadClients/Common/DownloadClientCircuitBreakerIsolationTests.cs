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
using Listenarr.Infrastructure.DependencyInjection.DownloadClients;
using Listenarr.Tests.Common;
using Polly.CircuitBreaker;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Common;

[Trait("Name", "DownloadClientCircuitBreakerIsolationTests")]
[Trait("Category", "Infrastructure")]
public sealed class DownloadClientCircuitBreakerIsolationTests : BaseTests
{
    // A Polly circuit breaker is stateful: its open/closed state and failure count live in the
    // policy instance. One instance shared across the download clients is one global breaker, so a
    // run of failures against qBittorrent stops Transmission, SABnzbd and NZBGet too.
    //
    // This drives the policies directly rather than through HttpClient. Going through the named
    // clients would also pass through the retry policy, whose backoff is 2, 4 and 8 seconds, so
    // opening a breaker that way costs about 45 seconds. The property that matters is whether two
    // clients share breaker state, and that is observable here in milliseconds.
    //
    // What this covers is the factory contract, that each call yields an independent breaker. It
    // does not cover the registration wiring, that AddDownloadClientHttpClients calls the factory
    // once per named client rather than hoisting one instance into a local and reusing it. Reading
    // that back would mean reflecting into the private policy field of Polly's
    // PolicyHttpMessageHandler, a Microsoft package detail that would break the suite on a package
    // bump for a reason unrelated to Listenarr, so the wiring is covered by review instead.
    [Fact]
    public async Task CircuitBreakerPolicies_DoNotShareStateBetweenClients()
    {
        var first = DownloadClientRegistrationExtensions.CreateCircuitBreakerPolicy();
        var second = DownloadClientRegistrationExtensions.CreateCircuitBreakerPolicy();

        Assert.NotSame(first, second);

        // Three consecutive transient failures is the configured threshold.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await first.ExecuteAsync(() =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        }

        await Assert.ThrowsAnyAsync<BrokenCircuitException>(() =>
            first.ExecuteAsync(() =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        // The second client is untouched by the first client's failures.
        var stillWorking = await second.ExecuteAsync(() =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal(HttpStatusCode.OK, stillWorking.StatusCode);
    }
}
