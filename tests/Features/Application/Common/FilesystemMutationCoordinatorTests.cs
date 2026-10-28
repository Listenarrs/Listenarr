using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Common;

[Trait("Name", "FilesystemMutationCoordinatorTests")]
[Trait("Category", "Application")]
public sealed class FilesystemMutationCoordinatorTests : BaseTests
{
    [Fact]
    public async Task ExecuteExclusiveAsync_AllowsOnlyOneOperationAtATime()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.ExecuteExclusiveAsync(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;

        var second = coordinator.ExecuteExclusiveAsync(_ =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_NestedCallIsReentrantWhileExternalCallerWaits()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var nestedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOuter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var externalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var outer = coordinator.ExecuteExclusiveAsync(async _ =>
        {
            await coordinator.ExecuteExclusiveAsync(_ =>
            {
                nestedEntered.SetResult();
                return Task.CompletedTask;
            });
            await releaseOuter.Task;
        });
        await nestedEntered.Task;

        var external = coordinator.ExecuteExclusiveAsync(_ =>
        {
            externalEntered.SetResult();
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        Assert.False(externalEntered.Task.IsCompleted);
        releaseOuter.SetResult();
        await Task.WhenAll(outer, external);
        Assert.True(externalEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_ParallelNestedCallsAreSerialized()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        await coordinator.ExecuteExclusiveAsync(async _ =>
        {
            var first = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
            await firstEntered.Task;
            var second = coordinator.ExecuteExclusiveAsync(_ =>
            {
                secondEntered = true;
                return Task.CompletedTask;
            });

            await Task.Delay(50);
            Assert.False(secondEntered);
            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second);
        });

        Assert.True(secondEntered);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_EscapedNestedCallHoldsGateUntilNestedScopeCompletes()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var nestedEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? nested = null;

        var outer = coordinator.ExecuteExclusiveAsync(async _ =>
        {
            nested = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                nestedEntered.TrySetResult();
                await releaseNested.Task;
            });
            await nestedEntered.Task;
        });
        await nestedEntered.Task;
        await Task.Yield();

        Assert.False(outer.IsCompleted);
        var independentEntered = false;
        var independent = coordinator.ExecuteExclusiveAsync(_ =>
        {
            independentEntered = true;
            return Task.CompletedTask;
        });
        await Task.Delay(50);
        Assert.False(independentEntered);

        releaseNested.TrySetResult();
        Assert.NotNull(nested);
        await Task.WhenAll(nested, outer, independent);
        Assert.True(independentEntered);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_CancelledWaiterDoesNotEnter()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEntered = false;
        var first = coordinator.ExecuteExclusiveAsync(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiter = coordinator.ExecuteExclusiveAsync(_ =>
        {
            waiterEntered = true;
            return Task.CompletedTask;
        }, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(waiterEntered);
        releaseFirst.SetResult();
        await first;
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_ReleasesGateAfterException()
    {
        using var coordinator = new FilesystemMutationCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteExclusiveAsync(_ =>
                throw new InvalidOperationException("expected")));

        var entered = false;
        await coordinator.ExecuteExclusiveAsync(_ =>
        {
            entered = true;
            return Task.CompletedTask;
        });
        Assert.True(entered);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_ReleasesGateAfterOperationCancellation()
    {
        using var coordinator = new FilesystemMutationCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteExclusiveAsync(_ => Task.FromCanceled(new CancellationToken(true))));

        var result = await coordinator.ExecuteExclusiveAsync(_ => Task.FromResult(42));
        Assert.Equal(42, result);
    }
}
