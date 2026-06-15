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
using System.Text.Json;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Listenarr.Tests.Features.Api
{
    public class SecurityPipelineEndToEndTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public SecurityPipelineEndToEndTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task AuthDisabled_AllowsApiEndpoint_WithoutCredentials()
        {
            var apiBase = TestUtils.ResolveApiBasePath(_factory.Services);
            using var client = CreateClient(_factory);

            var response = await client.GetAsync($"{apiBase}/library");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task AuthEnabled_RequiresCredentials_ForApiAndSignalRHubs()
        {
            using var factory = CreateFactory(authenticationRequired: "true", apiKey: "server-api-key");
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
            using var client = CreateClient(factory);

            var apiResponse = await client.GetAsync($"{apiBase}/library");
            await using var hubConnection = CreateHubConnection(factory);

            Assert.Equal(HttpStatusCode.Unauthorized, apiResponse.StatusCode);
            await Assert.ThrowsAnyAsync<Exception>(() => hubConnection.StartAsync());
        }

        [Fact]
        public async Task ApiKeyHeader_AllowsApiRequests_AndBypassesCsrfForUnsafeMethods()
        {
            const string apiKey = "server-api-key";
            using var factory = CreateFactory(authenticationRequired: "true", apiKey: apiKey);
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
            using var client = CreateClient(factory);

            using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            getRequest.Headers.Add("X-Api-Key", apiKey);
            var getResponse = await client.SendAsync(getRequest);

            using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/library/999/rename/preview");
            postRequest.Headers.Add("X-Api-Key", apiKey);
            var postResponse = await client.SendAsync(postRequest);

            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, postResponse.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, postResponse.StatusCode);
        }

        [Fact]
        public async Task SessionCookie_Authenticates_AndCsrfProtectsUnsafeMethods()
        {
            using var factory = CreateFactory(authenticationRequired: "true", apiKey: "server-api-key");
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
            using var client = CreateClient(factory);
            var sessionToken = await CreateSessionAsync(factory, "admin-user", isAdmin: true);

            using var unsafeWithoutCsrf = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/library/999/rename/preview");
            unsafeWithoutCsrf.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var missingCsrfResponse = await client.SendAsync(unsafeWithoutCsrf);

            var (csrfToken, antiforgeryCookie) = await GetAntiforgeryTokenAsync(client, apiBase, sessionToken);
            using var unsafeWithCsrf = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/library/999/rename/preview");
            unsafeWithCsrf.Headers.Add("Cookie", $"listenarr_session={sessionToken}; {antiforgeryCookie}");
            unsafeWithCsrf.Headers.Add("X-XSRF-TOKEN", csrfToken);
            var validCsrfResponse = await client.SendAsync(unsafeWithCsrf);

            Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, validCsrfResponse.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, validCsrfResponse.StatusCode);
        }

        [Fact]
        public async Task SignalRHub_AllowsApiKeyAccessToken_WhenAuthEnabled()
        {
            const string apiKey = "server-api-key";
            using var factory = CreateFactory(authenticationRequired: "true", apiKey: apiKey);
            await using var connection = CreateHubConnection(factory, accessToken: apiKey);

            await connection.StartAsync();

            Assert.Equal(HubConnectionState.Connected, connection.State);
        }

        [Fact]
        public async Task SignalRHub_AllowsSessionCookie_WhenAuthEnabled()
        {
            using var factory = CreateFactory(authenticationRequired: "true", apiKey: "server-api-key");
            var sessionToken = await CreateSessionAsync(factory, "admin-user", isAdmin: true);
            await using var connection = CreateHubConnection(factory, cookieHeader: $"listenarr_session={sessionToken}");

            await connection.StartAsync();

            Assert.Equal(HubConnectionState.Connected, connection.State);
        }

        [Fact]
        public async Task SignalRHub_AllowsAnonymous_WhenAuthDisabled()
        {
            await using var connection = CreateHubConnection(_factory);

            await connection.StartAsync();

            Assert.Equal(HubConnectionState.Connected, connection.State);
        }

        [Fact]
        public async Task SystemEndpoint_RequiresCredentials_WhenAuthEnabled()
        {
            using var factory = CreateFactory(authenticationRequired: "true", apiKey: "server-api-key");
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
            using var client = CreateClient(factory);

            var response = await client.GetAsync($"{apiBase}/system/info");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task SystemEndpoint_AllowsAnonymous_WhenAuthDisabled()
        {
            var apiBase = TestUtils.ResolveApiBasePath(_factory.Services);
            using var client = CreateClient(_factory);

            var response = await client.GetAsync($"{apiBase}/system/info");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task SystemEndpoint_AllowsSessionCookie_WhenAuthEnabled()
        {
            using var factory = CreateFactory(authenticationRequired: "true", apiKey: "server-api-key");
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
            using var client = CreateClient(factory);
            var sessionToken = await CreateSessionAsync(factory, "admin-user", isAdmin: true);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/system/info");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task ForwardedProto_MarksAntiforgeryCookieSecure_WhenProxyIsTrusted()
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.PostConfigure<ForwardedHeadersOptions>(options =>
                    {
                        options.KnownProxies.Add(IPAddress.Loopback);
                    });
                });
            });
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
            using var client = CreateClient(factory);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/antiforgery/token");
            request.Headers.Add("X-Forwarded-Proto", "https");
            var response = await client.SendAsync(request);

            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookieValues));
            Assert.Contains(setCookieValues, value =>
                value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)
                && value.Contains("secure", StringComparison.OrdinalIgnoreCase));
        }

        private WebApplicationFactory<Program> CreateFactory(string authenticationRequired, string? apiKey = null)
        {
            return _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupConfigService>(_ =>
                        new StartupConfigServiceMock(new StartupConfig
                        {
                            AuthenticationRequired = authenticationRequired,
                            ApiKey = apiKey,
                        }));
                });
            });
        }

        private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        {
            return factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });
        }

        private static async Task<string> CreateSessionAsync(
            WebApplicationFactory<Program> factory,
            string username,
            bool isAdmin)
        {
            using var scope = factory.Services.CreateScope();
            var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
            return await sessionService.CreateSessionAsync(username, isAdmin, false);
        }

        private static HubConnection CreateHubConnection(
            WebApplicationFactory<Program> factory,
            string? accessToken = null,
            string? cookieHeader = null)
        {
            var hubPath = "/hubs/logs";
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                hubPath += $"?access_token={Uri.EscapeDataString(accessToken)}";
            }

            return new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri("http://localhost"), hubPath), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();

                    if (!string.IsNullOrWhiteSpace(cookieHeader))
                    {
                        options.Headers.Add("Cookie", cookieHeader);
                    }
                })
                .Build();
        }

        private static async Task<(string Token, string Cookie)> GetAntiforgeryTokenAsync(
            HttpClient client,
            string apiBase,
            string sessionToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/antiforgery/token");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));

            Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookieValues));
            var antiforgeryCookie = setCookieValues
                .Select(value => value.Split(';', 2)[0])
                .FirstOrDefault(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));

            Assert.False(string.IsNullOrWhiteSpace(antiforgeryCookie));
            return (token!, antiforgeryCookie!);
        }
    }
}
