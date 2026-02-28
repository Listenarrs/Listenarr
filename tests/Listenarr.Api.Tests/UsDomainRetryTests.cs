using System;
using System.Net;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class UsDomainRetryTests
    {
        [Fact]
        public async Task Amazon_GetHtmlAsync_RetriesToCom_WhenNonUsHostRequested()
        {
            // Arrange: handler returns locale redirect for amazon.de and valid content for amazon.com
            var handler = new DelegatingHandlerStub(req =>
            {
                var host = req.RequestUri?.Host ?? string.Empty;
                if (host.Contains("amazon.de", StringComparison.OrdinalIgnoreCase) || host.Contains("amazon.co.uk", StringComparison.OrdinalIgnoreCase))
                {
                    var html = "<html><body><p>We have redirected you to amazon.de due to your location</p></body></html>";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
                }
                // Default: return a US product/search page
                var usHtml = "<html><head><title>US Amazon Page</title></head><body>Product Content</body></html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(usHtml) });
            });

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://www.amazon.de") };
            var svc = new AmazonAsinService(httpClient, new NullLogger<AmazonAsinService>(), new TestConfigurationService());

            // Call internal GetHtmlAsync directly (InternalsVisibleTo applied)
            var html = await svc.GetHtmlAsync("https://www.amazon.de/s?k=test", CancellationToken.None);

            Assert.NotNull(html);
            Assert.Contains("US Amazon Page", html);
        }
    }

    // Simple delegating handler stub to simulate network responses
    internal class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _responder(request);
        }
    }

    // Minimal test IConfigurationService implementation used by these unit tests
    internal class TestConfigurationService : IConfigurationService
    {
        public Task<List<ApiConfiguration>> GetApiConfigurationsAsync() => Task.FromResult(new List<ApiConfiguration>());
        public Task<ApiConfiguration?> GetApiConfigurationAsync(string id) => Task.FromResult<ApiConfiguration?>(null);
        public Task<string> SaveApiConfigurationAsync(ApiConfiguration config) => Task.FromResult(string.Empty);
        public Task<bool> DeleteApiConfigurationAsync(string id) => Task.FromResult(true);

        public Task<List<DownloadClientConfiguration>> GetDownloadClientConfigurationsAsync() => Task.FromResult(new List<DownloadClientConfiguration>());
        public Task<DownloadClientConfiguration?> GetDownloadClientConfigurationAsync(string id) => Task.FromResult<DownloadClientConfiguration?>(null);
        public Task<string> SaveDownloadClientConfigurationAsync(DownloadClientConfiguration config) => Task.FromResult(string.Empty);
        public Task<bool> DeleteDownloadClientConfigurationAsync(string id) => Task.FromResult(true);

        public Task<ApplicationSettings> GetApplicationSettingsAsync() => Task.FromResult(new ApplicationSettings());
        public Task<List<WebhookConfiguration>> GetWebhookConfigurationsAsync() => Task.FromResult(new List<WebhookConfiguration>());
        public Task SaveApplicationSettingsAsync(ApplicationSettings settings) => Task.CompletedTask;

        public Task<StartupConfig> GetStartupConfigAsync() => Task.FromResult(new StartupConfig());
        public Task SaveStartupConfigAsync(StartupConfig config) => Task.CompletedTask;
    }
}

