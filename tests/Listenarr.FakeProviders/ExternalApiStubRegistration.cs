using Microsoft.Extensions.Http;

namespace Listenarr.FakeProviders;

public static class ExternalApiStubRegistration
{
    /// <summary>
    /// Every client IHttpClientFactory builds with a production primary handler (Audible, Audnexus,
    /// OpenLibrary, Amazon, images, download clients...) sends its requests to
    /// <see cref="ExternalApiStubHandler"/> instead of the network, except for an explicitly enabled
    /// loopback qBittorrent sidecar in browser E2E tests.
    /// Audible is answered from the fixtures, anything else gets 404. A client whose primary handler a
    /// test replaced with its own mock keeps that mock.
    /// </summary>
    public static IServiceCollection AddListenarrExternalApiTestStub(this IServiceCollection services, bool enableQbittorrent = false)
    {
        // Post-configured, so this action runs after every registration's own handler setup: it sees
        // the primary handler each registration (or test) finally chose.
        services.PostConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
            {
                if (builder.PrimaryHandler is HttpClientHandler or SocketsHttpHandler)
                {
                    // Innermost, so the registration's own handlers (policies, logging) still run and
                    // the production primary handler stays inspectable.
                    builder.AdditionalHandlers.Add(new ExternalApiStubInterceptor(
                        enableQbittorrent && builder.Name == "qbittorrent"));
                }
            }));

        return services;
    }

    private sealed class ExternalApiStubInterceptor(bool enableQbittorrent) : DelegatingHandler
    {
        private static readonly HttpMessageInvoker Stub = new(new ExternalApiStubHandler(), disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (enableQbittorrent && request.RequestUri is { Scheme: "http", Host: "127.0.0.1", Port: 8080 } uri &&
                uri.AbsolutePath.StartsWith("/api/v2/", StringComparison.Ordinal))
            {
                return base.SendAsync(request, cancellationToken);
            }

            return Stub.SendAsync(request, cancellationToken);
        }
    }
}
