using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_MissingNestedTargetAncestors_AreNotCopiedAsContent()
    {
        var source = FileService.GetTempDirectory("content-move-nested-scaffold-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(source, "container", "nested", "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.False(Directory.Exists(Path.Join(target, "container")));
        Assert.False(Directory.Exists(Path.Join(target, "nested")));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var scaffolding = await db.MoveJobCreatedDirectories
            .AsNoTracking()
            .Where(directory => directory.MoveJobId == request.JobId)
            .OrderBy(directory => directory.Path)
            .ToListAsync();
        Assert.Equal(2, scaffolding.Count);
        Assert.All(scaffolding, directory =>
            Assert.Equal(MoveCreatedDirectoryState.Created, directory.State));
    }

    [Fact]
    public async Task MoveContentsAsync_RetryAfterRemovedScaffolding_ReacquiresAndRetainsLedger()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-retry-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var firstScaffold = Path.Join(source, "container");
        var secondScaffold = Path.Join(firstScaffold, "nested");
        var target = Path.Join(secondScaffold, "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            db.MoveJobCreatedDirectories.AddRange(
                new MoveJobCreatedDirectory
                {
                    MoveJobId = request.JobId,
                    Path = firstScaffold,
                    State = MoveCreatedDirectoryState.Removed
                },
                new MoveJobCreatedDirectory
                {
                    MoveJobId = request.JobId,
                    Path = secondScaffold,
                    State = MoveCreatedDirectoryState.Removed
                });
            await db.SaveChangesAsync();
        }
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        await service.MoveContentsAsync(request, CancellationToken.None);
        await service.RetainTargetScaffoldingAsync(request, CancellationToken.None);

        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.False(File.Exists(Path.Join(firstScaffold, ".listenarr-scaffold-owner.json")));
        await using var verification = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var scaffolding = await verification.MoveJobCreatedDirectories
            .AsNoTracking()
            .Where(directory => directory.MoveJobId == request.JobId)
            .ToListAsync();
        Assert.Equal(2, scaffolding.Count);
        Assert.All(scaffolding, directory =>
            Assert.Equal(MoveCreatedDirectoryState.Retained, directory.State));
    }

    [Fact]
    public async Task MoveContentsAsync_PersistedScaffoldWithUnexpectedContent_FailsClosed()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-content-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var scaffold = Path.Join(source, "container");
        var target = Path.Join(scaffold, "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            db.MoveJobCreatedDirectories.Add(new MoveJobCreatedDirectory
            {
                MoveJobId = request.JobId,
                Path = scaffold,
                State = MoveCreatedDirectoryState.Planned
            });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(scaffold);
        await File.WriteAllTextAsync(Path.Join(scaffold, "operator-note.txt"), "keep me");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("unexpected content", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(scaffold, "operator-note.txt")));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [Theory]
    [InlineData(nameof(TargetScaffoldCleanupFaultPoint.BeforeQuarantineRename))]
    [InlineData(nameof(TargetScaffoldCleanupFaultPoint.AfterQuarantineRename))]
    [InlineData(nameof(TargetScaffoldCleanupFaultPoint.BeforeQuarantineValidation))]
    [InlineData(nameof(TargetScaffoldCleanupFaultPoint.BeforeQuarantineDelete))]
    [InlineData(nameof(TargetScaffoldCleanupFaultPoint.AfterQuarantineDelete))]
    [InlineData(nameof(TargetScaffoldCleanupFaultPoint.BeforeRemovedStateUpdate))]
    public async Task CleanupTerminalTargetScaffoldingAsync_RetryRecoversEveryMutationBoundary(
        string faultPointName)
    {
        var faultPoint = Enum.Parse<TargetScaffoldCleanupFaultPoint>(faultPointName);
        var state = await CreateEmptyTargetScaffoldAsync();
        var failingService = CreateMoveService(
            new ThrowTargetScaffoldCleanupOnce(faultPoint));

        await Assert.ThrowsAsync<IOException>(() =>
            failingService.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        var recoveryService = _provider.GetRequiredService<AudiobookContentMoveService>();
        await recoveryService.CleanupTerminalTargetScaffoldingAsync(
            state.Request,
            CancellationToken.None);

        Assert.False(Directory.Exists(state.PublishedRoot));
        Assert.False(Directory.Exists(state.Quarantine));
        await AssertScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Removed);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_ContentAddedAfterRename_IsPreserved()
    {
        var state = await CreateEmptyTargetScaffoldAsync();
        var unexpectedFile = Path.Join(state.Quarantine, "operator-note.txt");
        var service = CreateMoveService(
            new AddUnexpectedQuarantineContentAfterRename(unexpectedFile));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        Assert.Contains("unexpected file content", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(state.Quarantine));
        Assert.Equal("preserve", await File.ReadAllTextAsync(unexpectedFile));
        await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_ContentAddedBeforeRename_RetainsAndRemovesUnusedTombstone()
    {
        var state = await CreateEmptyTargetScaffoldAsync();
        var operatorFile = Path.Join(state.Request.Target, "operator-note.txt");
        var tombstone = Path.Join(
            Path.GetDirectoryName(state.Quarantine)!,
            $".listenarr-target-scaffold-quarantine-{state.Request.JobId:N}.cleanup.json");
        var service = CreateMoveService(
            new AddPublishedContentBeforeQuarantineRename(operatorFile));

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        Assert.True(File.Exists(tombstone));
        Assert.True(File.Exists(operatorFile));
        await _provider.GetRequiredService<AudiobookContentMoveService>()
            .CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None);

        Assert.True(File.Exists(operatorFile));
        Assert.False(File.Exists(tombstone));
        await AssertScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Retained);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_PartialQuarantineDeletion_ResumesFromTombstone()
    {
        var state = await CreateEmptyTargetScaffoldAsync();
        var tombstone = Path.Join(
            Path.GetDirectoryName(state.Quarantine)!,
            $".listenarr-target-scaffold-quarantine-{state.Request.JobId:N}.cleanup.json");
        var service = CreateMoveService(
            new ThrowOnTargetScaffoldFaultInvocation(
                TargetScaffoldCleanupFaultPoint.DuringQuarantineDelete,
                throwOnInvocation: 2));

        await Assert.ThrowsAsync<IOException>(() =>
            service.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        Assert.True(Directory.Exists(state.Quarantine));
        Assert.True(File.Exists(tombstone));
        await AssertScaffoldingNotRemovedAsync(state.Request.JobId);

        await _provider.GetRequiredService<AudiobookContentMoveService>()
            .CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None);

        Assert.False(Directory.Exists(state.Quarantine));
        Assert.False(File.Exists(tombstone));
        await AssertScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Removed);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_ReplacedQuarantineGeneration_IsPreserved()
    {
        var state = await CreateEmptyTargetScaffoldAsync();
        var originalGeneration = state.Quarantine + $"-original-{Guid.NewGuid():N}";
        var replacementFile = Path.Join(state.Quarantine, "operator-file.txt");
        var service = CreateMoveService(
            new ReplaceScaffoldRootBeforeRetirement(
                state.Quarantine,
                originalGeneration,
                replacementFile));

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        Assert.True(Directory.Exists(originalGeneration));
        Assert.Equal("preserve", await File.ReadAllTextAsync(replacementFile));
        await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_PartialStateUpdate_ResumesRemainingRows()
    {
        var state = await CreateEmptyTargetScaffoldAsync();
        var failingService = CreateMoveService(
            new ThrowOnTargetScaffoldFaultInvocation(
                TargetScaffoldCleanupFaultPoint.BeforeRemovedStateUpdate,
                throwOnInvocation: 2));

        await Assert.ThrowsAsync<IOException>(() =>
            failingService.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        Assert.False(Directory.Exists(state.Quarantine));
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            var states = await db.MoveJobCreatedDirectories
                .AsNoTracking()
                .Where(directory => directory.MoveJobId == state.Request.JobId)
                .Select(directory => directory.State)
                .ToListAsync();
            Assert.Contains(MoveCreatedDirectoryState.Removed, states);
            Assert.Contains(states, candidate => candidate is
                MoveCreatedDirectoryState.Created or MoveCreatedDirectoryState.Planned);
        }

        await _provider.GetRequiredService<AudiobookContentMoveService>()
            .CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None);

        await AssertScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Removed);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_PartialCleanupIntentPersistence_RecoversBeforeRename()
    {
        var state = await CreateEmptyTargetScaffoldAsync();
        await SetScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Retained);
        var failingService = CreateMoveService(
            new ThrowOnTargetScaffoldFaultInvocation(
                TargetScaffoldCleanupFaultPoint.BeforeCleanupIntentStateUpdate,
                throwOnInvocation: 2));

        await Assert.ThrowsAsync<IOException>(() =>
            failingService.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        Assert.True(Directory.Exists(state.PublishedRoot));
        Assert.False(Directory.Exists(state.Quarantine));
        await _provider.GetRequiredService<AudiobookContentMoveService>()
            .CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None);

        Assert.False(Directory.Exists(state.PublishedRoot));
        Assert.False(Directory.Exists(state.Quarantine));
        await AssertScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Removed);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_RetainedRowsAreNormalizedBeforeQuarantineDeletion()
    {
        var state = await CreateQuarantinedTargetScaffoldAsync();
        await SetScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Retained);
        var failingService = CreateMoveService(
            new ThrowOnTargetScaffoldFaultInvocation(
                TargetScaffoldCleanupFaultPoint.BeforeRemovedStateUpdate,
                throwOnInvocation: 1));

        await Assert.ThrowsAsync<IOException>(() =>
            failingService.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));

        Assert.False(Directory.Exists(state.PublishedRoot));
        Assert.False(Directory.Exists(state.Quarantine));
        await AssertScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Created);
        await _provider.GetRequiredService<AudiobookContentMoveService>()
            .CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None);
        await AssertScaffoldingStateAsync(
            state.Request.JobId,
            MoveCreatedDirectoryState.Removed);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_RecreatedPublishedRoot_PreservesBothArtifacts()
    {
        var state = await CreateQuarantinedTargetScaffoldAsync();
        Directory.CreateDirectory(state.PublishedRoot);
        await File.WriteAllTextAsync(
            Path.Join(state.PublishedRoot, "operator-file.txt"),
            "preserve");

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            _provider.GetRequiredService<AudiobookContentMoveService>()
                .CleanupTerminalTargetScaffoldingAsync(
                    state.Request,
                    CancellationToken.None));

        Assert.Contains("both", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(state.Quarantine));
        Assert.True(File.Exists(Path.Join(state.PublishedRoot, "operator-file.txt")));
        await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    public async Task CleanupTerminalTargetScaffoldingAsync_InvalidQuarantineMarker_PreservesArtifact(
        string markerState)
    {
        var state = await CreateQuarantinedTargetScaffoldAsync();
        var markerPath = Path.Join(state.Quarantine, ".listenarr-scaffold-owner.json");
        if (markerState == "missing")
        {
            File.Delete(markerPath);
        }
        else
        {
            await File.WriteAllTextAsync(
                markerPath,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Version = 1,
                    JobId = Guid.NewGuid(),
                    TargetPath = state.Request.Target,
                    PublishedRoot = state.PublishedRoot
                }));
        }

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            _provider.GetRequiredService<AudiobookContentMoveService>()
                .CleanupTerminalTargetScaffoldingAsync(
                    state.Request,
                    CancellationToken.None));

        Assert.True(Directory.Exists(state.Quarantine));
        await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
    }

    [Fact]
    public async Task CleanupTerminalTargetScaffoldingAsync_MarkerReplacedBeforePinnedRead_PreservesArtifact()
    {
        var state = await CreateQuarantinedTargetScaffoldAsync();
        var markerPath = Path.Join(state.Quarantine, ".listenarr-scaffold-owner.json");
        var replacement = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            JobId = Guid.NewGuid(),
            TargetPath = state.Request.Target,
            PublishedRoot = state.PublishedRoot
        });
        var replaced = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(path =>
        {
            if (replaced
                || !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(state.Quarantine),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            replaced = true;
            File.Delete(markerPath);
            File.WriteAllText(markerPath, replacement);
        });

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            _provider.GetRequiredService<AudiobookContentMoveService>()
                .CleanupTerminalTargetScaffoldingAsync(
                    state.Request,
                    CancellationToken.None));

        Assert.True(replaced);
        Assert.Equal(replacement, await File.ReadAllTextAsync(markerPath));
        Assert.True(Directory.Exists(state.Quarantine));
        await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
    }

    [LinuxFact]
    public async Task CleanupTerminalTargetScaffoldingAsync_AmbiguousPersistedMarkerPath_PreservesArtifact()
    {
        var state = await CreateQuarantinedTargetScaffoldAsync();
        var markerPath = Path.Join(state.Quarantine, ".listenarr-scaffold-owner.json");
        var ambiguousTarget = "/" + Path.GetFullPath(state.Request.Target);
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousTarget,
            out _));
        await File.WriteAllTextAsync(
            markerPath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = state.Request.JobId,
                TargetPath = ambiguousTarget,
                PublishedRoot = Path.GetFullPath(state.PublishedRoot)
            }));

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            _provider.GetRequiredService<AudiobookContentMoveService>()
                .CleanupTerminalTargetScaffoldingAsync(
                    state.Request,
                    CancellationToken.None));

        Assert.True(Directory.Exists(state.Quarantine));
        Assert.True(File.Exists(markerPath));
        await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
    }

    [DirectoryLinkFact]
    public async Task CleanupTerminalTargetScaffoldingAsync_DanglingQuarantineLink_IsNotTreatedAsRemoved()
    {
        var state = await CreateQuarantinedTargetScaffoldAsync();
        Directory.Delete(state.Quarantine, recursive: true);
        var external = FileService.GetTempDirectory("content-move-scaffold-dangling-link");
        Assert.True(
            TryCreateDirectoryLink(state.Quarantine, external),
            "The required directory link could not be created.");
        Directory.Delete(external, recursive: true);

        try
        {
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                _provider.GetRequiredService<AudiobookContentMoveService>()
                    .CleanupTerminalTargetScaffoldingAsync(
                        state.Request,
                        CancellationToken.None));

            await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
        }
        finally
        {
            TryRemoveDirectoryLink(state.Quarantine);
        }
    }

    [DirectoryLinkFact]
    public async Task CleanupTerminalTargetScaffoldingAsync_LinkInsideQuarantine_PreservesExternalTree()
    {
        var state = await CreateQuarantinedTargetScaffoldAsync();
        var external = FileService.GetTempDirectory("content-move-scaffold-link-external");
        var externalFile = await FileService.GetFileAsync(external, "keep.txt", "preserve");
        var link = Path.Join(state.Quarantine, "linked");
        Assert.True(
            TryCreateDirectoryLink(link, external),
            "The required directory link could not be created.");

        try
        {
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                _provider.GetRequiredService<AudiobookContentMoveService>()
                    .CleanupTerminalTargetScaffoldingAsync(
                        state.Request,
                        CancellationToken.None));

            Assert.True(Directory.Exists(state.Quarantine));
            Assert.True(File.Exists(externalFile));
            await AssertScaffoldingNotRemovedAsync(state.Request.JobId);
        }
        finally
        {
            TryRemoveDirectoryLink(link);
        }
    }

    private async Task<TargetScaffoldCleanupState> CreateEmptyTargetScaffoldAsync()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-cleanup-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var publishedRoot = Path.Join(source, "container");
        var target = Path.Join(publishedRoot, "nested", "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await _provider.GetRequiredService<AudiobookContentMoveService>()
            .MoveContentsAsync(request, CancellationToken.None);
        Directory.Delete(target, recursive: true);
        var quarantine = Path.Join(
            source,
            $".listenarr-scaffold-cleanup-{request.JobId:N}");
        return new TargetScaffoldCleanupState(
            request,
            publishedRoot,
            quarantine);
    }

    private async Task<TargetScaffoldCleanupState> CreateQuarantinedTargetScaffoldAsync()
    {
        var state = await CreateEmptyTargetScaffoldAsync();
        var service = CreateMoveService(
            new ThrowTargetScaffoldCleanupOnce(
                TargetScaffoldCleanupFaultPoint.AfterQuarantineRename));
        await Assert.ThrowsAsync<IOException>(() =>
            service.CleanupTerminalTargetScaffoldingAsync(
                state.Request,
                CancellationToken.None));
        Assert.False(Directory.Exists(state.PublishedRoot));
        Assert.True(Directory.Exists(state.Quarantine));
        return state;
    }

    private async Task SetScaffoldingStateAsync(
        Guid jobId,
        MoveCreatedDirectoryState state)
    {
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var directories = await db.MoveJobCreatedDirectories
            .Where(directory => directory.MoveJobId == jobId)
            .ToListAsync();
        Assert.NotEmpty(directories);
        foreach (var directory in directories)
        {
            directory.State = state;
        }
        await db.SaveChangesAsync();
    }

    private async Task AssertScaffoldingStateAsync(
        Guid jobId,
        MoveCreatedDirectoryState expected)
    {
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var states = await db.MoveJobCreatedDirectories
            .AsNoTracking()
            .Where(directory => directory.MoveJobId == jobId)
            .Select(directory => directory.State)
            .ToListAsync();
        Assert.NotEmpty(states);
        Assert.All(states, state => Assert.Equal(expected, state));
    }

    private async Task AssertScaffoldingNotRemovedAsync(Guid jobId)
    {
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var states = await db.MoveJobCreatedDirectories
            .AsNoTracking()
            .Where(directory => directory.MoveJobId == jobId)
            .Select(directory => directory.State)
            .ToListAsync();
        Assert.NotEmpty(states);
        Assert.DoesNotContain(MoveCreatedDirectoryState.Removed, states);
    }

    private sealed record TargetScaffoldCleanupState(
        AudiobookContentMoveRequest Request,
        string PublishedRoot,
        string Quarantine);

    private sealed class AddPublishedContentBeforeQuarantineRename(
        string operatorFile) : IMoveFaultInjector
    {
        public void OnTargetScaffoldCleanup(
            Guid jobId,
            TargetScaffoldCleanupFaultPoint faultPoint)
        {
            if (faultPoint != TargetScaffoldCleanupFaultPoint.BeforeQuarantineRename)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(operatorFile)!);
            File.WriteAllText(operatorFile, "preserve");
        }
    }

    private sealed class AddUnexpectedQuarantineContentAfterRename(
        string unexpectedFile) : IMoveFaultInjector
    {
        public void OnTargetScaffoldCleanup(
            Guid jobId,
            TargetScaffoldCleanupFaultPoint faultPoint)
        {
            if (faultPoint == TargetScaffoldCleanupFaultPoint.AfterQuarantineRename)
            {
                File.WriteAllText(unexpectedFile, "preserve");
            }
        }
    }

    private sealed class ThrowTargetScaffoldCleanupOnce(
        TargetScaffoldCleanupFaultPoint expected) : IMoveFaultInjector
    {
        private bool _thrown;

        public void OnTargetScaffoldCleanup(
            Guid jobId,
            TargetScaffoldCleanupFaultPoint faultPoint)
        {
            if (_thrown || faultPoint != expected)
            {
                return;
            }

            _thrown = true;
            throw new IOException($"Injected target scaffold cleanup failure at {faultPoint}.");
        }
    }

    private sealed class ThrowOnTargetScaffoldFaultInvocation(
        TargetScaffoldCleanupFaultPoint expected,
        int throwOnInvocation) : IMoveFaultInjector
    {
        private int _invocations;

        public void OnTargetScaffoldCleanup(
            Guid jobId,
            TargetScaffoldCleanupFaultPoint faultPoint)
        {
            if (faultPoint != expected)
            {
                return;
            }

            _invocations++;
            if (_invocations == throwOnInvocation)
            {
                throw new IOException("Injected partial target scaffold state update failure.");
            }
        }
    }

    private sealed class ReplaceScaffoldRootBeforeRetirement(
        string quarantinePath,
        string originalGeneration,
        string replacementFile) : IMoveFaultInjector
    {
        private int _invocations;

        public void OnTargetScaffoldCleanup(
            Guid jobId,
            TargetScaffoldCleanupFaultPoint faultPoint)
        {
            if (faultPoint != TargetScaffoldCleanupFaultPoint.DuringQuarantineDelete)
            {
                return;
            }

            _invocations++;
            if (_invocations != 3)
            {
                return;
            }

            Directory.Move(quarantinePath, originalGeneration);
            Directory.CreateDirectory(quarantinePath);
            File.WriteAllText(replacementFile, "preserve");
        }
    }
}
