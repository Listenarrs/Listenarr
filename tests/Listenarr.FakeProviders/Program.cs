using Listenarr.Api.Startup;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Infrastructure.Realtime.DependencyInjection;

namespace Listenarr.FakeProviders;

public static class FakeProvidersHost
{
    public static async Task Main(string[] args)
    {
        var realtimeLogSink = RealtimeLoggingExtensions.CreateListenarrRealtimeLogSink();
        var bootstrapFileSystem = new LocalFileSystem();
        var builder = ListenarrBuilderFactory.Create(args, realtimeLogSink, bootstrapFileSystem);

        builder.AddListenarrApiServices(bootstrapFileSystem);
        builder.Services.AddListenarrInfrastructureComposition(builder.Configuration, builder.Environment);
        builder.Services.AddListenarrExternalApiTestStub();

        var app = builder.Build();

        app.Services.ApplyListenarrDatabaseMigrations();
        await app.RunListenarrStartupTasksAsync();

        realtimeLogSink.InitializeListenarrRealtimeLogging(app.Services);

        app.UseListenarrRequestPipeline(endpoints => endpoints.MapListenarrRealtimeHubs(app.Environment));

        app.Run();
    }
}
