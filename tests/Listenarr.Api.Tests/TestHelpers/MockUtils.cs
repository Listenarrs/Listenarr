using System.Net;
using System.Text;
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Listenarr.Api.Tests
{
    public abstract class MockUtils
    {
        /// <summary>
        /// Returns a 200 HTTP reply with the given JSON as content
        /// </summary>
        /// <param name="json">JSON structure to use as a response body</param>
        /// <returns>HttpResponseMessage with status 200 and JSON body</returns>
        public static HttpResponseMessage GetCannedResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        public static Mock<DownloadMonitorService> GetDownloadMonitorServiceMock()
        {
            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            scopeFactoryMock.Setup(x => x.CreateScope()).Returns(scopeMock.Object);
            scopeMock.Setup(x => x.ServiceProvider).Returns(serviceProviderMock.Object);

            return new Mock<DownloadMonitorService>(
                scopeFactoryMock.Object,
                new Mock<IHubContext<DownloadHub>>().Object,
                new Mock<ILogger<DownloadMonitorService>>().Object,
                new Mock<IHttpClientFactory>().Object,
                new Mock<IAppMetricsService>().Object);
        }
    }
}