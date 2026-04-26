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
using System.Text;
using Asp.Versioning.ApiExplorer;
using Listenarr.Domain.Models;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Api.Middleware
{
    public class AuthenticationMiddlewareTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public AuthenticationMiddlewareTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task ProtectedEndpoint_Returns401_WhenUnauthenticated_AndAuthRequired()
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            });
            var apiBasePath = ResolveApiBasePath(factory.Services);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var resp = await client.GetAsync($"{apiBasePath}/library");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task StartupConfig_Returns401_WhenUnauthenticated_AndAuthRequired()
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            });
            var apiBasePath = ResolveApiBasePath(factory.Services);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var resp = await client.GetAsync($"{apiBasePath}/configuration/startupconfig");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task GenerateInitialApiKey_Returns401_WhenUnauthenticated_AndAuthRequired()
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            });
            var apiBasePath = ResolveApiBasePath(factory.Services);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync($"{apiBasePath}/configuration/apikey/generate-initial", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task ProwlarrPostIndexers_Returns401_WhenUnauthenticated_AndAuthRequired()
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            });
            var apiBasePath = ResolveApiBasePath(factory.Services);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var content = new StringContent("[]", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync($"{apiBasePath}/indexers", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        private static string ResolveApiBasePath(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider.GetService<IApiVersionDescriptionProvider>();
            var groupName = provider?.ApiVersionDescriptions.FirstOrDefault(d => !d.IsDeprecated)?.GroupName
                ?? provider?.ApiVersionDescriptions.FirstOrDefault()?.GroupName;

            return string.IsNullOrWhiteSpace(groupName) ? "/api/v1" : $"/api/{groupName}";
        }
    }
}
