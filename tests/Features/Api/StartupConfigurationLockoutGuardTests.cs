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
using System.Text;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Api
{
    [Trait("Area", "StartupConfigurationApi")]
    [Trait("Name", "StartupConfigurationLockoutGuardTests")]
    [Trait("Category", "StartupConfiguration")]
    public sealed class StartupConfigurationLockoutGuardTests(ListenarrWebApplicationFactory factory)
        : BaseTests, IClassFixture<ListenarrWebApplicationFactory>
    {

        [Fact]
        public async Task SaveStartupConfig_ReturnsBadRequest_WhenEnablingLoginWithoutAdminUser()
        {
            // The settings screen only offers the config.json download fallback for
            // non-4xx failures, so a refusal by the lockout guard must not be a 500.
            using var factory = CreateFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

            var response = await PostStartupConfigAsync(client, "true");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Cannot enable the login screen", body);
            Assert.Contains("admin_user_required", body);
        }

        [Fact]
        public async Task SaveStartupConfig_Succeeds_WhenLoginStaysDisabled()
        {
            // Control: the same request path with the guard not triggered must succeed,
            // so the BadRequest above is attributable to the guard.
            using var factory = CreateFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

            var response = await PostStartupConfigAsync(client, "false");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private WebApplicationFactory<Program> CreateFactory()
        {
            return factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupConfigService>(_ =>
                        new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "false" }));
                });
            });
        }

        private static async Task<HttpResponseMessage> PostStartupConfigAsync(HttpClient client, string authenticationRequired)
        {
            var csrfToken = await GetAntiforgeryTokenAsync(client);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration/startupconfig")
            {
                Content = new StringContent(
                    $$"""{ "authenticationRequired": "{{authenticationRequired}}" }""", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
        {
            var tokenResponse = await client.GetAsync("/api/v1/antiforgery/token");
            tokenResponse.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            return token!;
        }
    }
}
