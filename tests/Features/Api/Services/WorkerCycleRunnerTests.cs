using Listenarr.Infrastructure.HostedServices;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "WorkerCycleRunnerTests")]
    [Trait("Category", "BackgroundWorkers")]
    public class WorkerCycleRunnerTests
    {
        [Fact]
        public async Task RunPeriodicAsync_SuccessfulCycle_EmitsStartedAndCompleted()
        {
            var metrics = new Mock<IAppMetricsService>();
            using var cts = new CancellationTokenSource();
            var runner = CreateRunner(metrics);

            await runner.RunPeriodicAsync(
                "ExampleWorker",
                initialDelay: null,
                intervalProvider: () => TimeSpan.FromMinutes(10),
                runCycle: _ =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                },
                cts.Token);

            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.started", It.IsAny<double>()), Times.Once);
            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.completed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task RunPeriodicAsync_NonFatalFailure_EmitsFailed()
        {
            var metrics = new Mock<IAppMetricsService>();
            using var cts = new CancellationTokenSource();
            var runner = CreateRunner(metrics);

            await runner.RunPeriodicAsync(
                "ExampleWorker",
                initialDelay: null,
                intervalProvider: () => TimeSpan.FromMinutes(10),
                runCycle: _ =>
                {
                    cts.Cancel();
                    throw new InvalidOperationException("cycle failed");
                },
                cts.Token);

            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.started", It.IsAny<double>()), Times.Once);
            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.failed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task RunPeriodicAsync_CanceledBeforeInitialDelay_EmitsSkipped()
        {
            var metrics = new Mock<IAppMetricsService>();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var runner = CreateRunner(metrics);

            await runner.RunPeriodicAsync(
                "ExampleWorker",
                initialDelay: TimeSpan.FromMinutes(10),
                intervalProvider: () => TimeSpan.FromMinutes(10),
                runCycle: _ => Task.CompletedTask,
                cts.Token);

            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.skipped", It.IsAny<double>()), Times.Once);
            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.started", It.IsAny<double>()), Times.Never);
        }

        [Fact]
        public async Task RunPeriodicAsync_ShutdownDuringCycle_StopsWithoutFailureMetric()
        {
            var metrics = new Mock<IAppMetricsService>();
            using var cts = new CancellationTokenSource();
            var runner = CreateRunner(metrics);

            await runner.RunPeriodicAsync(
                "ExampleWorker",
                initialDelay: null,
                intervalProvider: () => TimeSpan.FromMinutes(10),
                runCycle: async cancellationToken =>
                {
                    await cts.CancelAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                cts.Token);

            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.skipped", It.IsAny<double>()), Times.Once);
            metrics.Verify(m => m.Increment("worker.exampleworker.cycle.failed", It.IsAny<double>()), Times.Never);
        }

        private static WorkerCycleRunner CreateRunner(Mock<IAppMetricsService> metrics) =>
            new(TimeProvider.System, metrics.Object, Mock.Of<ILogger<WorkerCycleRunner>>());
    }
}
