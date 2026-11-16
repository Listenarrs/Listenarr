using Microsoft.Extensions.Http;

namespace Listenarr.TestHost.Metadata;

public static class ExternalApiStubRegistration
{
    /// <summary>
    /// Every client IHttpClientFactory builds (Audible, Audnexus, OpenLibrary, Amazon, images, download
    /// clients...) sends its requests to <see cref="ExternalApiStubHandler"/>, whatever primary handler its
    /// own registration chose: no request of the test host leaves it. Audible is answered from the
    /// fixtures, anything else gets 404.
    /// </summary>
    public static IServiceCollection AddListenarrExternalApiTestStub(this IServiceCollection services)
    {
        // Post-configured, so this action runs after every registration's own handler setup and wins.
        services.PostConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.PrimaryHandler = new ExternalApiStubHandler()));

        return services;
    }
}
