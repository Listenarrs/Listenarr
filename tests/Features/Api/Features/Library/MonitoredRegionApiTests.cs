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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Hosting;
using Microsoft.Extensions.Http;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "MonitoredRegionApiTests")]
    [Trait("Category", "Api")]
    public sealed class MonitoredRegionApiTests : BaseTests
    {
        private sealed class AudibleHostRecorder(ConcurrentBag<string> hosts) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.RequestUri is { } uri && uri.Host.Contains("audible.", StringComparison.OrdinalIgnoreCase))
                {
                    hosts.Add(uri.Host);
                }

                return base.SendAsync(request, cancellationToken);
            }
        }

        [Theory]
        [InlineData("api/v1/authors/monitoring", "Edgar Rice Burroughs", "B0C6B525PQ")]
        [InlineData("api/v1/series/monitoring", "Pellucidar", "B0C6B525PQ")]
        public async Task MonitorWithUnsupportedRegion_UsesTheUsCatalogueAndSavesTheBookWithRegionUs(
            string monitoringRoute,
            string name,
            string addedAsin)
        {
            // Given the API host, with every request to Audible recorded
            var audibleHosts = new ConcurrentBag<string>();
            using var factory = new ListenarrWebApplicationFactory().WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.PostConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
                            // Outermost, so it sees the request before the fixture stub answers it.
                            handlerBuilder.AdditionalHandlers.Insert(0, new AudibleHostRecorder(audibleHosts))))));
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                db.QualityProfiles.Add(new QualityProfile { Name = "Standard", IsDefault = true });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();

            // When it is monitored with a region Audible has no endpoint for
            using var tokenResponse = await client.GetAsync("api/v1/antiforgery/token");
            tokenResponse.EnsureSuccessStatusCode();
            var csrfToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
            using var request = new HttpRequestMessage(HttpMethod.Post, monitoringRoute)
            {
                Content = JsonContent.Create(new { name, region = "zz", language = "english" })
            };
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
            using var response = await client.SendAsync(request);

            // Then the catalogue lookup reached the US endpoint only
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Assert.NotEmpty(audibleHosts);
            Assert.All(audibleHosts, host => Assert.Equal("api.audible.com", host));

            // And the monitor is stored and found under the submitted region
            var status = await client.GetFromJsonAsync<JsonElement>(
                $"{monitoringRoute}/status?name={Uri.EscapeDataString(name)}&region=zz&language=english");
            Assert.True(status.GetProperty("isMonitored").GetBoolean());

            // And the library lists the added book with region us and nothing broken
            var library = await client.GetFromJsonAsync<JsonElement>("api/v1/library");
            var book = Assert.Single(
                library.EnumerateArray(),
                item => item.GetProperty("asin").GetString() == addedAsin);
            Assert.Equal("us", book.GetProperty("asinRegion").GetString());
            Assert.DoesNotContain(
                book.GetProperty("brokenReasons").EnumerateArray(),
                reason => reason.GetString() == "missing_region");
        }
    }
}
