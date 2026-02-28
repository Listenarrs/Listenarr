using System.Net;
using System.Text;
using System.Threading.Tasks;
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
            using var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var resp = await client.GetAsync("/api/library");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task StartupConfig_Returns401_WhenUnauthenticated_AndAuthRequired()
        {
            var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var resp = await client.GetAsync("/api/configuration/startupconfig");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task GenerateInitialApiKey_Returns401_WhenUnauthenticated_AndAuthRequired()
        {
            var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/configuration/apikey/generate-initial", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task ProwlarrPostIndexers_Returns401_WhenUnauthenticated_AndAuthRequired()
        {
            using var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<Listenarr.Api.Services.IStartupConfigService>(sp =>
                    {
                        return new TestStartupConfigService(new StartupConfig { AuthenticationRequired = "Enabled" });
                    });
                });
            }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var content = new StringContent("[]", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/v1/indexers", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    internal class TestStartupConfigService : Listenarr.Api.Services.IStartupConfigService
    {
        private readonly StartupConfig _cfg;
        public TestStartupConfigService(StartupConfig cfg) { _cfg = cfg; }
        public StartupConfig? GetConfig() => _cfg;
        public Task ReloadAsync() => Task.CompletedTask;
        public Task SaveAsync(StartupConfig config) => Task.CompletedTask;
    }
}
