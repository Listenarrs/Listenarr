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
using System.Text.Json;
using Listenarr.Api.Controllers;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;

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
        public async Task SessionBearerHeader_NoLongerAuthenticates()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

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

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task SessionTokenHeader_NoLongerAuthenticates()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

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

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/library");
            request.Headers.Add("X-Session-Token", sessionToken);
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
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
        public async Task Login_SetsSessionCookie_Without_ReturningReadableSessionSecrets()
        {
            var username = $"cookie-user-{Guid.NewGuid():N}";
            const string sessionToken = "server-session-cookie-value";

            var userService = new Mock<IUserService>(MockBehavior.Strict);
            userService
                .Setup(x => x.ValidateCredentialsAsync(username, It.IsAny<string>()))
                .ReturnsAsync(true);
            userService
                .Setup(x => x.GetByUsernameAsync(username))
                .ReturnsAsync(new User { Username = username, IsAdmin = true });

            var rateLimiter = new Mock<ILoginRateLimiter>(MockBehavior.Strict);
            rateLimiter.Setup(x => x.IsBlocked(It.IsAny<string>())).Returns(false);
            rateLimiter.Setup(x => x.RecordSuccess(It.IsAny<string>()));

            var startupConfigService = new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "true" });

            var sessionService = new Mock<ISessionService>(MockBehavior.Strict);
            sessionService
                .Setup(x => x.CreateSessionAsync(username, true, false))
                .ReturnsAsync(sessionToken);

            var controller = new AccountController(
                startupConfigService,
                NullLogger<AccountController>.Instance,
                userService.Object,
                rateLimiter.Object,
                sessionService.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var result = await controller.Login(new LoginRequest
            {
                Username = username,
                Password = "TestPassword!123",
                RememberMe = false,
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains(controller.Response.Headers.SetCookie.ToArray(),
                header => header.Contains("listenarr_session=", StringComparison.Ordinal));

            var payload = JsonSerializer.SerializeToElement(ok.Value);
            Assert.Equal("session", payload.GetProperty("authType").GetString());
            Assert.False(payload.TryGetProperty("sessionToken", out _));
            Assert.False(payload.TryGetProperty("imageToken", out _));
            Assert.False(payload.TryGetProperty("imageTokenExpiresAt", out _));

            userService.VerifyAll();
            rateLimiter.VerifyAll();
            sessionService.VerifyAll();
        }

        [Fact]
        public async Task RemovedImageTokenEndpoint_ReturnsNotFound()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

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

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/account/image-token");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task QueryToken_DoesNot_Authenticate_ImageEndpoint()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            var resp = await client.GetAsync($"{apiBase}/images/B00TOKEN01?t=stale-or-invalid-token");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task BootstrapEndpoint_IsPublic_AndDoesNotExposeSecrets()
        {
            using var factory = CreateAuthEnabledFactory("true", apiKey: "server-api-key");
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            var resp = await client.GetAsync($"{apiBase}/configuration/bootstrap");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(payload.RootElement.TryGetProperty("authenticationRequired", out var authRequired));
            Assert.True(authRequired.GetBoolean());
            Assert.True(payload.RootElement.TryGetProperty("apiVersion", out _));
            Assert.False(payload.RootElement.TryGetProperty("apiKey", out _));
            Assert.False(payload.RootElement.TryGetProperty("ApiKey", out _));
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
        public async Task ApiKeyMiddleware_OverridesAuthenticatedSessionPrincipal_WhenValidApiKeyProvided()
        {
            var apiKey = "test-api-key-12345";
            using var factory = CreateAuthEnabledFactory("true", apiKey);
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
            const string username = "cookie-admin";

            string sessionToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                sessionToken = await sessionService.CreateSessionAsync(username, true, false);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/account/me");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            request.Headers.Add("X-Api-Key", apiKey);
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(payload.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.Equal("ApiKey", payload.RootElement.GetProperty("name").GetString());
        }

        [Fact]
        public async Task FullStartupConfig_RequiresElevatedAccess_WhenAuthenticationEnabled()
        {
            using var factory = CreateAuthEnabledFactory("true", apiKey: "test-api-key-12345");
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            string sessionToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                sessionToken = await sessionService.CreateSessionAsync("regular-user", false, false);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/configuration/startupconfig");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        [Fact]
        public async Task ApiKeyEndpoint_AllowsAdminSession_WhenAuthenticationEnabled()
        {
            using var factory = CreateAuthEnabledFactory("true", apiKey: "server-api-key");
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            string sessionToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                sessionToken = await sessionService.CreateSessionAsync("admin-user", true, false);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/configuration/apikey");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("server-api-key", payload.RootElement.GetProperty("apiKey").GetString());
        }

        [Fact]
        public async Task ApiKeyEndpoint_RejectsApiKey_WhenAuthenticationEnabled()
        {
            const string apiKey = "server-api-key";
            using var factory = CreateAuthEnabledFactory("true", apiKey);
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/configuration/apikey");
            request.Headers.Add("X-Api-Key", apiKey);
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        [Fact]
        public async Task RegenerateApiKey_RejectsApiKey_WhenAuthenticationEnabled()
        {
            const string apiKey = "server-api-key";
            using var factory = CreateAuthEnabledFactory("true", apiKey);
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/configuration/apikey/regenerate");
            request.Headers.Add("X-Api-Key", apiKey);
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        [Fact]
        public async Task RegenerateApiKey_AllowsAdminSession_WhenAuthenticationEnabled()
        {
            const string apiKey = "server-api-key";
            using var factory = CreateAuthEnabledFactory("true", apiKey);
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            string sessionToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                sessionToken = await sessionService.CreateSessionAsync("admin-user", true, false);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            var (csrfToken, antiforgeryCookie) = await GetAntiforgeryTokenAsync(client, apiBase, sessionToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/configuration/apikey/regenerate");
            request.Headers.Add("Cookie", $"listenarr_session={sessionToken}; {antiforgeryCookie}");
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var regeneratedApiKey = payload.RootElement.GetProperty("apiKey").GetString();
            Assert.False(string.IsNullOrWhiteSpace(regeneratedApiKey));
            Assert.NotEqual(apiKey, regeneratedApiKey);
        }

        [Fact]
        public async Task ProwlarrCompatibility_RequiresApiKey_WhenAuthenticationEnabled()
        {
            const string apiKey = "prowlarr-api-key";
            using var factory = CreateAuthEnabledFactory("true", apiKey);

            string sessionToken;
            using (var scope = factory.Services.CreateScope())
            {
                var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
                sessionToken = await sessionService.CreateSessionAsync("admin-user", true, false);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            using var cookieRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/prowlarr/system/status");
            cookieRequest.Headers.Add("Cookie", $"listenarr_session={sessionToken}");
            var cookieResp = await client.SendAsync(cookieRequest);
            Assert.Equal(HttpStatusCode.Forbidden, cookieResp.StatusCode);

            using var apiKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/prowlarr/system/status");
            apiKeyRequest.Headers.Add("X-Api-Key", apiKey);
            var apiKeyResp = await client.SendAsync(apiKeyRequest);
            Assert.Equal(HttpStatusCode.OK, apiKeyResp.StatusCode);
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

        [Fact]
        public async Task ReadyEndpoint_AllowsAnonymous_WhenAuthEnabled()
        {
            using var factory = CreateAuthEnabledFactory();
            var apiBase = TestUtils.ResolveApiBasePath(factory.Services);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

            var resp = await client.GetAsync($"{apiBase}/system/ready");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        private WebApplicationFactory<Program> CreateAuthEnabledFactory(string authenticationRequired = "true", string? apiKey = null)
        {
            return _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupConfigService>(sp =>
                    {
                        return new StartupConfigServiceMock(new StartupConfig
                        {
                            AuthenticationRequired = authenticationRequired,
                            ApiKey = apiKey,
                        });
                    });
                });
            });
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
