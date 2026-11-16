using Microsoft.Extensions.Http;

namespace Listenarr.FakeProviders;

public static class ExternalApiStubRegistration
{
    /// <summary>
    /// Every client IHttpClientFactory builds with a production primary handler (Audible, Audnexus,
    /// OpenLibrary, Amazon, images, download clients...) sends its requests to
    /// <see cref="ExternalApiStubHandler"/> instead of the network: no request of the host leaves it.
    /// Audible is answered from the fixtures, anything else gets 404. A client whose primary handler a
    /// test replaced with its own mock keeps that mock.
    /// </summary>
    public static IServiceCollection AddListenarrExternalApiTestStub(this IServiceCollection services)
    {
        // Post-configured, so this action runs after every registration's own handler setup: it sees
        // the primary handler each registration (or test) finally chose.
        services.PostConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
            {
                if (builder.PrimaryHandler is HttpClientHandler or SocketsHttpHandler)
                {
                    // Innermost, so the registration's own handlers (policies, logging) still run and
                    // the production primary handler stays inspectable, but is never reached.
                    builder.AdditionalHandlers.Add(new ExternalApiStubInterceptor());
                }
            }));

        return services;
    }

    /// <summary>Answers from <see cref="ExternalApiStubHandler"/> without calling its inner handler.</summary>
    private sealed class ExternalApiStubInterceptor : DelegatingHandler
    {
        private static readonly HttpMessageInvoker Stub = new(new ExternalApiStubHandler(), disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Stub.SendAsync(request, cancellationToken);
    }
}
