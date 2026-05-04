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
using System.Net.Http.Headers;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Api
{
    public class SessionCookieAuthTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public SessionCookieAuthTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task CookieAuth_ProtectedEndpoint_Succeeds_WithValidCookie()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            // Create a session directly via the service (bypasses login controller)
            string sessionToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                sessionToken = await sessionService.CreateSessionAsync("testuser", true, false);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            // Send request with session token as cookie (no Bearer header)
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task CookieAuth_ProtectedEndpoint_Returns401_WithInvalidCookie()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            request.Headers.Add("Cookie", "listenarr_session=invalid-garbage-token");
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task CookieAuth_InvalidatedSession_Returns401()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            // Create and then invalidate a session
            string sessionToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                sessionToken = await sessionService.CreateSessionAsync("testuser", true, false);
                await sessionService.InvalidateSessionAsync(sessionToken);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            // Cookie with invalidated token should return 401
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task CookieAuth_BearerTakesPriority_OverCookie()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            // Create two sessions: one valid (for Bearer), one invalidated (for cookie)
            string validToken;
            string invalidatedToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                validToken = await sessionService.CreateSessionAsync("testuser", true, false);
                invalidatedToken = await sessionService.CreateSessionAsync("testuser", true, false);
                await sessionService.InvalidateSessionAsync(invalidatedToken);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            // Valid Bearer + invalidated cookie → should succeed (Bearer wins)
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
            request.Headers.Add("Cookie", $"listenarr_session={invalidatedToken}");
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task CookieAuth_NoCookieOrBearer_Returns401()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            var resp = await client.GetAsync($"{apiBase}/library");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task ApiKeyHeader_AuthenticatesSuccessfully()
        {
            var apiKey = "test-api-key-12345";
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupConfigService>(sp =>
                    {
                        return new StartupConfigServiceMock(new StartupConfig
                        {
                            AuthenticationRequired = "true",
                            ApiKey = apiKey,
                        });
                    });
                });
            });
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            request.Headers.Add("X-Api-Key", apiKey);
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task AuthDisabled_EndpointAccessible_WithoutCredentials()
        {
            // Base factory already sets AuthenticationRequired = "false"
            var apiBase = TestUtils.ResolveApiBasePath(_factory.Services);

            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            // No Bearer, no cookie, no API key — should still succeed when auth is disabled
            var resp = await client.GetAsync($"{apiBase}/library");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        private WebApplicationFactory<Program> CreateAuthEnabledFactory()
        {
            return _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupConfigService>(sp =>
                    {
                        return new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "true" });
                    });
                });
            });
        }
    }
}
