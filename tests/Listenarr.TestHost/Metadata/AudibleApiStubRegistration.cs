using Listenarr.Application.Metadata.Audible;

namespace Listenarr.TestHost.Metadata;

public static class AudibleApiStubRegistration
{
    public static IServiceCollection AddListenarrAudibleTestStub(this IServiceCollection services)
    {
        services.AddHttpClient<AudibleService>()
            .ConfigurePrimaryHttpMessageHandler(() => new AudibleApiStubHandler());

        return services;
    }
}
