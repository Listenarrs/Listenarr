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
using System.Net.Http.Json;
using System.Text.Json;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "MonitoredRegionUpgradeApiTests")]
    [Trait("Category", "Api")]
    public sealed class MonitoredRegionUpgradeApiTests : BaseTests
    {
        public static TheoryData<string, string, string, string, bool> LegacyMonitors => new()
        {
            { "authors", "Edgar Rice Burroughs", "zz", "english", true },
            { "authors", "Edgar Rice Burroughs", "ca", "english", true },
            { "series", "Pellucidar", "zz", "english", false },
            { "series", "Pellucidar", "ca", "english", false }
        };

        [Theory]
        [MemberData(nameof(LegacyMonitors))]
        public async Task LegacyMonitorRow_IsFoundByStatusAndReusedWhenMonitoringIsEnabledAgain(
            string kind,
            string name,
            string storedRegion,
            string language,
            bool isAuthor)
        {
            // Given a monitor row stored under its region the way an earlier version left it
            using var factory = new ListenarrWebApplicationFactory();
            int legacyId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                db.QualityProfiles.Add(new QualityProfile { Name = "Standard", IsDefault = true });
                if (isAuthor)
                {
                    var author = new MonitoredAuthor
                    {
                        AuthorName = name,
                        AuthorNameNormalized = name.ToLowerInvariant(),
                        Region = storedRegion,
                        Language = language
                    };
                    db.MonitoredAuthors.Add(author);
                    await db.SaveChangesAsync();
                    legacyId = author.Id;
                }
                else
                {
                    var series = new MonitoredSeries
                    {
                        SeriesName = name,
                        SeriesNameNormalized = name.ToLowerInvariant(),
                        Region = storedRegion,
                        Language = language
                    };
                    db.MonitoredSeries.Add(series);
                    await db.SaveChangesAsync();
                    legacyId = series.Id;
                }
            }

            using var client = factory.CreateClient();
            var monitorProperty = isAuthor ? "monitoredAuthor" : "monitoredSeries";

            // When the status endpoint is asked about it
            var status = await client.GetFromJsonAsync<JsonElement>(
                $"api/v1/{kind}/monitoring/status?name={Uri.EscapeDataString(name)}&region={storedRegion}&language={language}");

            // Then it reports the original row
            Assert.True(status.GetProperty("isMonitored").GetBoolean());
            Assert.Equal(legacyId, status.GetProperty(monitorProperty).GetProperty("id").GetInt32());

            // And enabling monitoring again returns the original id
            using var tokenResponse = await client.GetAsync("api/v1/antiforgery/token");
            tokenResponse.EnsureSuccessStatusCode();
            var csrfToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/{kind}/monitoring")
            {
                Content = JsonContent.Create(new { name, region = storedRegion, language })
            };
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
            using var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var enabled = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(legacyId, enabled.GetProperty(monitorProperty).GetProperty("id").GetInt32());

            // And exactly one row remains, still under the stored region
            using var verifyScope = factory.Services.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            var storedRegions = isAuthor
                ? await verifyDb.MonitoredAuthors.Select(a => a.Region).ToListAsync()
                : await verifyDb.MonitoredSeries.Select(s => s.Region).ToListAsync();
            Assert.Equal(storedRegion, Assert.Single(storedRegions));
        }
    }
}
