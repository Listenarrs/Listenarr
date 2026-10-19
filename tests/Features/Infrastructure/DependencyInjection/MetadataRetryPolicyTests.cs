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
using Listenarr.Infrastructure.DependencyInjection.Metadata;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Infrastructure.DependencyInjection
{
    /// <summary>
    /// Polly's HandleTransientHttpError covers 5xx and 408 but not 429, which is the only status a
    /// rate limiter returns. Without an explicit clause a throttled metadata lookup fails on the
    /// first attempt and the caller records it as "no match found" rather than "ask again".
    /// </summary>
    [Trait("Area", "Metadata")]
    [Trait("Name", "MetadataRetryPolicyTests")]
    [Trait("Category", "DependencyInjection")]
    public class MetadataRetryPolicyTests : BaseTests
    {
        [Fact]
        public async Task AudibleClient_TooManyRequests_IsRetriedUntilItSucceeds()
        {
            var handler = new SequencedHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
            var services = new ServiceCollection();
            services.AddMetadataHttpClients(new ConfigurationManager());
            services.AddHttpClient<AudibleService>().ConfigurePrimaryHttpMessageHandler(() => handler);
            using var provider = services.BuildServiceProvider();

            var client = provider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(AudibleService));
            using var response = await client.GetAsync("https://example.invalid/search");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, handler.Attempts);
        }

        [Fact]
        public async Task AudibleClient_RetryAfterHint_IsPreferredOverTheShorterBackoff()
        {
            var handler = new SequencedHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK)
            {
                RetryAfter = TimeSpan.FromSeconds(3)
            };
            var services = new ServiceCollection();
            services.AddMetadataHttpClients(new ConfigurationManager());
            services.AddHttpClient<AudibleService>().ConfigurePrimaryHttpMessageHandler(() => handler);
            using var provider = services.BuildServiceProvider();

            var client = provider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(AudibleService));
            var started = DateTimeOffset.UtcNow;
            using var response = await client.GetAsync("https://example.invalid/search");
            var elapsed = DateTimeOffset.UtcNow - started;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The first-attempt backoff is 2s; the server asked for 3s and must win.
            Assert.True(
                elapsed >= TimeSpan.FromSeconds(2.5),
                $"Expected the Retry-After hint to be honored, but the retry ran after {elapsed}.");
        }

        [Fact]
        public void OpenLibraryClient_IsRegisteredWithTheSharedRetryPolicy()
        {
            var services = new ServiceCollection();

            services.AddMetadataServices();

            using var provider = services.BuildServiceProvider();
            var options = provider
                .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get(nameof(IOpenLibraryService));

            // A bare AddScoped registration resolves the default unnamed client, which carries no
            // policy at all - the OpenLibrary fallback then had no retry of any kind.
            Assert.NotEmpty(options.HttpMessageHandlerBuilderActions);
        }

        private sealed class SequencedHandler : HttpMessageHandler
        {
            private readonly Queue<HttpStatusCode> _statuses;

            public SequencedHandler(params HttpStatusCode[] statuses) =>
                _statuses = new Queue<HttpStatusCode>(statuses);

            public TimeSpan? RetryAfter { get; init; }

            public int Attempts { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Attempts++;
                var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
                var response = new HttpResponseMessage(status);
                if (status == HttpStatusCode.TooManyRequests && RetryAfter is { } retryAfter)
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
                }

                return Task.FromResult(response);
            }
        }
    }
}
