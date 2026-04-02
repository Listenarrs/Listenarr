using System.Net;
using System.Text;
using System.Threading.Tasks;
using Asp.Versioning.ApiExplorer;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Api.Tests
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
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
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
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
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
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
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
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
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
