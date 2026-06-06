using System.Diagnostics;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Name", "DownloadMonitorServiceResilienceTests")]
    [Trait("Category", "DownloadMonitorService")]
    public class DownloadMonitorServiceResilienceTests
    {
        // Regression guard for the host restart loop: a transient failure inside a monitor cycle
        // (e.g. a SQLite "database is locked" exception thrown while persisting a download) must be
        // logged-and-swallowed so the poll loop keeps running. The loop previously caught only
        // OperationCanceledException, so any other exception propagated out of ExecuteAsync and
        // tripped BackgroundServiceExceptionBehavior.StopHost, gracefully restarting the whole app
        // on every poll cycle.
        //
        // Drives the extracted loop (RunMonitorLoopAsync) directly to avoid the service's fixed 5s
        // startup delay, keeping the test fast and free of timing pressure on the parallel suite.
        [Fact]
        [Trait("Method", "RunMonitorLoopAsync")]
        public async Task RunMonitorLoopAsync_CycleThrows_KeepsLoopingInsteadOfStoppingHost()
        {
            int cycleAttempts = 0;

            var config = new Mock<IConfigurationService>();
            // Every cycle fails the way the live host did: a non-cancellation exception raised while
            // the cycle is talking to the database.
            config.Setup(c => c.GetDownloadClientConfigurationsAsync())
                  .Returns(() =>
                  {
                      Interlocked.Increment(ref cycleAttempts);
                      throw new InvalidOperationException("simulated transient SQLite 'database is locked'");
                  });
            config.Setup(c => c.GetApplicationSettingsAsync())
                  .ReturnsAsync(new ApplicationSettings());

            var provider = new Mock<IServiceProvider>();
            provider.Setup(p => p.GetService(typeof(IConfigurationService))).Returns(config.Object);
            provider.Setup(p => p.GetService(typeof(IDownloadRepository))).Returns(Mock.Of<IDownloadRepository>());
            provider.Setup(p => p.GetService(typeof(IDownloadClientGateway))).Returns(Mock.Of<IDownloadClientGateway>());

            var scope = new Mock<IServiceScope>();
            scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            var service = new DownloadMonitorService(
                scopeFactory.Object,
                Mock.Of<IDownloadPushService>(),
                NullLogger<DownloadMonitorService>.Instance);

            using var cts = new CancellationTokenSource();
            var loopTask = service.RunMonitorLoopAsync(cts.Token);

            // Wait for the first cycle to be attempted (and thrown + swallowed). With the default
            // polling interval the loop then parks in its inter-cycle delay rather than completing.
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref cycleAttempts) < 1 && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20);
            }

            Assert.True(cycleAttempts >= 1, "The monitor loop never executed a cycle.");
            // If the cycle's exception had propagated (the bug), the loop task would be faulted/
            // completed by now. It must still be running.
            Assert.False(loopTask.IsCompleted,
                "RunMonitorLoopAsync stopped after a cycle threw - the exception propagated instead of being swallowed.");

            cts.Cancel();
            await loopTask; // must complete cleanly (no exception escapes the loop)
        }
    }
}
