using Listenarr.Api.Startup;
using Listenarr.FakeProviders.Routes;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Infrastructure.DependencyInjection.Workers;
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Infrastructure.Realtime.DependencyInjection;

namespace Listenarr.FakeProviders;

public static class FakeProvidersHost
{
    /// <summary>
    /// Registers the unmatched scan worker for test hosts that run with background services disabled.
    /// The worker registration is internal to Infrastructure; this assembly is a friend, while the
    /// backend-integration test assembly is not, so it reaches the worker through here.
    /// </summary>
    public static void AddUnmatchedScanWorker(IServiceCollection services) =>
        services.AddUnmatchedScanWorker();

    public static async Task Main(string[] args)
    {
        var realtimeLogSink = RealtimeLoggingExtensions.CreateListenarrRealtimeLogSink();
        var bootstrapFileSystem = new LocalFileSystem();
        var builder = ListenarrBuilderFactory.Create(args, realtimeLogSink, bootstrapFileSystem);

        builder.AddListenarrApiServices(bootstrapFileSystem);
        builder.Services.AddListenarrInfrastructureComposition(builder.Configuration, builder.Environment);
        // The background workers are off in this host (LISTENARR_DISABLE_HOSTED_SERVICES); the import
        // tests driving this host still need the unmatched scan to run.
        builder.Services.AddUnmatchedScanWorker();
        var enableDownloadScenarios = builder.Configuration[DownloadScenarioEndpoints.EnableVariable] == "1";
        builder.Services.AddListenarrExternalApiTestStub(enableDownloadScenarios);

        var app = builder.Build();

        app.Services.ApplyListenarrDatabaseMigrations();
        await app.RunListenarrStartupTasksAsync();

        realtimeLogSink.InitializeListenarrRealtimeLogging(app.Services);

        app.UseListenarrRequestPipeline(endpoints => endpoints.MapListenarrRealtimeHubs(app.Environment));
        if (enableDownloadScenarios)
        {
            DownloadScenarioEndpoints.Map(app);
        }

        app.Run();
    }
}
