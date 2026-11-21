using Listenarr.Application.Downloads.Contracts;
using Listenarr.Application.Downloads.Contracts.Repositories;
using Listenarr.Application.Downloads.Submission;
using Listenarr.Domain.Downloads;
using Listenarr.Infrastructure.Downloads.Monitoring;

namespace Listenarr.FakeProviders.Routes;

internal static class DownloadScenarioEndpoints
{
    public const string EnableVariable = "LISTENARR_E2E_QBITTORRENT";

    // This private, trackerless torrent carries no media and cannot contact peers.
    private const string Torrent = "d4:infod6:lengthi1e4:name9:probe.m4b12:piece lengthi16384e6:pieces20:000000000000000000007:privatei1eee";

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/test/downloads");
        routes.MapPost("/reference", (IDownloadReferenceService references) =>
        {
            var source = new DownloadSourceDescriptor(null, null, DownloadProtocol.Torrent,
                [new DownloadSourceLocator(DownloadSourceLocatorKind.TorrentBytes,
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Torrent)), "probe.torrent")]);
            var candidate = new TrustedDownloadCandidate("e2e-probe", "Download E2E probe", "", "",
                "E2E fixture", null, null, 1, null, source);
            return Results.Ok(new { downloadReference = references.Create(candidate) });
        });

        routes.MapPost("/{id}/reconcile", async (string id, IDownloadRepository downloads,
            IServiceProvider services, CancellationToken ct) =>
        {
            var download = await downloads.GetByIdAsync(id);
            if (download == null) return Results.NotFound();

            // Age only the fixture row and use a fresh processor so the test does not wait
            // for either the grace period or the next scheduled sweep.
            download.StartedAt = DateTime.UtcNow.AddMinutes(-6);
            await downloads.UpdateAsync(download);
            await ActivatorUtilities.CreateInstance<DownloadMonitorProcessor>(services).RunCycleAsync(ct);
            return Results.NoContent();
        });
    }
}
