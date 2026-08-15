using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs;

[Trait("Name", "AudiobookOperationCoordinatorTests")]
[Trait("Category", "Application")]
public sealed class AudiobookOperationCoordinatorTests : BaseTests
{
    [Fact]
    public async Task SameAudiobookOperations_AreSerialized()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;
        var second = coordinator.ExecuteExclusiveAsync(
            42,
            _ =>
            {
                secondEntered = true;
                return Task.CompletedTask;
            });

        await Task.Delay(50);
        Assert.False(secondEntered);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task ManyAudiobooks_DoNotUseRecursiveLockDepth()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var audiobookIds = Enumerable.Range(1, 10_000).Reverse().ToArray();
        var entered = false;

        await coordinator.ExecuteExclusiveAsync(
            audiobookIds,
            _ =>
            {
                entered = true;
                return Task.CompletedTask;
            });

        Assert.True(entered);
    }

    [Fact]
    public async Task OverlappingSets_UseCanonicalOrder()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.ExecuteExclusiveAsync(
            [2, 1],
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;

        var second = coordinator.ExecuteExclusiveAsync(
            [1, 2],
            _ =>
            {
                secondEntered.TrySetResult();
                return Task.CompletedTask;
            });
        await Task.Delay(50);
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NestedLowerKeyAcquisition_ThrowsInsteadOfDeadlocking()
    {
        using var coordinator = new AudiobookOperationCoordinator();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteExclusiveAsync(
                2,
                _ => coordinator.ExecuteExclusiveAsync(
                    [1, 2],
                    _ => Task.CompletedTask)));

        Assert.Contains("higher key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NestedHigherKeyAcquisition_RemainsAllowedInCanonicalOrder()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var nestedEntered = false;

        await coordinator.ExecuteExclusiveAsync(
            1,
            _ => coordinator.ExecuteExclusiveAsync(
                [1, 2],
                _ =>
                {
                    nestedEntered = true;
                    return Task.CompletedTask;
                }));

        Assert.True(nestedEntered);
    }

    [Fact]
    public async Task DifferentAudiobooks_CanRunConcurrently()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;
        var second = coordinator.ExecuteExclusiveAsync(
            43,
            _ =>
            {
                secondEntered.TrySetResult();
                return Task.CompletedTask;
            });

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task NestedSameAudiobookOperations_RemainReentrantUntilOuterScopeExits()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var nestedCount = 0;

        await coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                await coordinator.ExecuteExclusiveAsync(
                    42,
                    _ =>
                    {
                        nestedCount++;
                        return Task.CompletedTask;
                    });
                await coordinator.ExecuteExclusiveAsync(
                    42,
                    _ =>
                    {
                        nestedCount++;
                        return Task.CompletedTask;
                    });
            });

        Assert.Equal(2, nestedCount);
    }

    [Fact]
    public async Task ParallelNestedSameAudiobookOperations_AreSerialized()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        await coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                var first = coordinator.ExecuteExclusiveAsync(
                    42,
                    async _ =>
                    {
                        firstEntered.TrySetResult();
                        await releaseFirst.Task;
                    });
                await firstEntered.Task;
                var second = coordinator.ExecuteExclusiveAsync(
                    42,
                    _ =>
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
    public async Task EscapedNestedHigherKeyOperation_HoldsInheritedLowerKey()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var nestedEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? nested = null;

        var outer = coordinator.ExecuteExclusiveAsync(
            1,
            async _ =>
            {
                nested = coordinator.ExecuteExclusiveAsync(
                    2,
                    async _ =>
                    {
                        nestedEntered.TrySetResult();
                        await releaseNested.Task;
                    });
                await nestedEntered.Task;
            });
        await nestedEntered.Task;
        await Task.Yield();

        Assert.False(outer.IsCompleted);
        var lowerKeyEntered = false;
        var independent = coordinator.ExecuteExclusiveAsync(
            1,
            _ =>
            {
                lowerKeyEntered = true;
                return Task.CompletedTask;
            });
        await Task.Delay(50);
        Assert.False(lowerKeyEntered);

        releaseNested.TrySetResult();
        Assert.NotNull(nested);
        await Task.WhenAll(nested, outer, independent);
        Assert.True(lowerKeyEntered);
    }

    [Fact]
    public async Task EscapedNestedOperation_HoldsGateUntilNestedScopeCompletes()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var nestedEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? nested = null;

        var outer = coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                nested = coordinator.ExecuteExclusiveAsync(
                    42,
                    async _ =>
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
        var independent = coordinator.ExecuteExclusiveAsync(
            42,
            _ =>
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
    public async Task CanceledMultiKeyWaiter_ReleasesAlreadyAcquiredKeys()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var secondKeyEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondKey = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = coordinator.ExecuteExclusiveAsync(
            2,
            async _ =>
            {
                secondKeyEntered.TrySetResult();
                await releaseSecondKey.Task;
            });
        await secondKeyEntered.Task;

        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.ExecuteExclusiveAsync(
            [1, 2],
            _ => Task.CompletedTask,
            cancellation.Token);
        await Task.Delay(50);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var keyOneEntered = false;
        await coordinator.ExecuteExclusiveAsync(
            1,
            _ =>
            {
                keyOneEntered = true;
                return Task.CompletedTask;
            });
        Assert.True(keyOneEntered);

        releaseSecondKey.TrySetResult();
        await blocker;
    }

    [Fact]
    public async Task AlreadyCanceledToken_ThrowsOperationCanceledExceptionBeforeQueueing()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ExecuteExclusiveAsync(
                42,
                _ => Task.CompletedTask,
                cancellation.Token));
    }

    [Fact]
    public async Task CanceledWaiter_ReleasesItsEntryReference()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;

        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.ExecuteExclusiveAsync(
            42,
            _ => Task.CompletedTask,
            cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        releaseFirst.TrySetResult();
        await first;

        var entriesField = typeof(AudiobookOperationCoordinator).GetField(
            "_entries",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var entries = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            entriesField!.GetValue(coordinator));
        Assert.Empty(entries);
    }
}
