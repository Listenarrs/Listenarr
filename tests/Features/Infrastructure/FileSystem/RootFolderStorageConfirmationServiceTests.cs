using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "RootFolderStorageConfirmationServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class RootFolderStorageConfirmationServiceTests : BaseTests
{
    [Fact]
    public async Task ConfirmCurrentFolderAsync_HealthyRoot_DoesNotRequirePhysicalAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-current-folder");
        await using var cleanup = fixture;
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);
        Assert.Equal(RootFolderStorageState.Healthy, observation.State);
        Assert.Null(observation.ConfirmationToken);

        var confirmed = await fixture.ConfirmCurrentAsync(root);

        Assert.Null(confirmed.DirectoryObjectIdentityVersion);
        Assert.Null(confirmed.DirectoryObjectIdentity);
        Assert.Equal(1, confirmed.StorageContractRevision);
        var persisted = await fixture.LoadRootAsync();
        var refreshed = await fixture.HealthResolver.ResolveAsync(persisted);
        Assert.Equal(RootFolderStorageState.Healthy, refreshed.State);
        Assert.True(refreshed.CanMutateFilesystem);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_LegacyRootBootstrapsFilesystemSemanticsWithoutPhysicalAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-legacy-root");
        await using var cleanup = fixture;
        await fixture.UpdateRootAsync(root =>
        {
            root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            root.ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown;
            root.PathIdentityState = PathIdentityState.Unavailable;
            root.PathIdentityKey = null;
        });
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);
        Assert.Equal(RootFolderStorageState.Unconfirmed, observation.State);
        Assert.NotNull(observation.ConfirmationToken);

        var confirmed = await fixture.Service.ConfirmCurrentFolderAsync(
            root.Id,
            root.Path,
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(root));

        Assert.Equal(PathIdentityState.Valid, confirmed.PathIdentityState);
        Assert.NotEqual(FileSystemCaseSensitivity.Unknown, confirmed.ResolvedCaseSensitivity);
        Assert.False(string.IsNullOrWhiteSpace(confirmed.PathIdentityKey));
        Assert.Null(confirmed.DirectoryObjectIdentityVersion);
        Assert.Null(confirmed.DirectoryObjectIdentity);
        var refreshed = await fixture.HealthResolver.ResolveAsync(await fixture.LoadRootAsync());
        Assert.Equal(RootFolderStorageState.Healthy, refreshed.State);
        Assert.True(refreshed.CanMutateFilesystem);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_StaleConfigurationToken_DoesNotAuthorizeUpdatedContract()
    {
        var fixture = await CreateFixtureAsync("confirm-stale-token");
        await using var cleanup = fixture;
        var root = await fixture.LoadRootAsync();
        var staleToken =
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(root);
        await fixture.UpdateRootAsync(candidate =>
            candidate.StorageContractRevision++);
        var current = await fixture.LoadRootAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                current.Id,
                current.Path,
                staleToken));

        Assert.Contains(
            "changed after it was displayed",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentityVersion);
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_FilesystemSemanticsChanged_RequiresPathChangeWorkflow()
    {
        var fixture = await CreateFixtureAsync("confirm-semantics-changed");
        await using var cleanup = fixture;
        var opposite = FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity
            == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
        await fixture.UpdateRootAsync(root =>
        {
            root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            root.ResolvedCaseSensitivity = opposite;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                root.Path,
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    opposite));
        });
        var root = await fixture.LoadRootAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                "stale-observation-token"));

        Assert.Contains("path-change workflow", exception.Message, StringComparison.OrdinalIgnoreCase);
        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ActiveMoveTouchesRoot_BlocksBeforeAuthorization()
    {
        var fixture = await CreateFixtureAsync(
            "confirm-active-move",
            blockingJobsFactory: rootPath =>
            [
                new MoveJob
                {
                    Id = Guid.NewGuid(),
                    SourcePath = Path.Join(rootPath, "Author", "Title"),
                    RequestedPath = Path.Join(Path.GetDirectoryName(rootPath)!, "elsewhere"),
                    Status = MoveJobStatus.Running
                }
            ]);
        await using var cleanup = fixture;
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [WindowsFact]
    public async Task ConfirmCurrentFolderAsync_ActiveMoveWithDeviceAliasSourceUnderRoot_BlocksBeforeAuthorization()
    {
        var fixture = await CreateFixtureAsync(
            "confirm-active-move-device-alias",
            blockingJobsFactory: rootPath =>
            [
                new MoveJob
                {
                    Id = Guid.NewGuid(),
                    SourcePath = @"\\?\" + Path.Join(rootPath, "Author", "Title"),
                    RequestedPath = Path.Join(Path.GetDirectoryName(rootPath)!, "elsewhere"),
                    Status = MoveJobStatus.Running
                }
            ]);
        await using var cleanup = fixture;
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ActiveVerifiedOrganizeRecovery_BlocksStorageRevisionChange()
    {
        var fixture = await CreateFixtureAsync("confirm-verified-organize-recovery");
        await using var cleanup = fixture;
        await fixture.AddVerifiedOrganizeRecoveryAsync();
        var root = await fixture.LoadRootAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ConfirmCurrentAsync(root));

        Assert.Contains("verified organize recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        var persisted = await fixture.LoadRootAsync();
        Assert.Equal(root.StorageContractRevision, persisted.StorageContractRevision);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ActiveRegistrationRecoveryUnderRoot_BlocksBeforeAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-registration-recovery");
        await using var cleanup = fixture;
        await fixture.AddRegistrationRecoveryAsync();
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);

        var exception = await Assert.ThrowsAsync<RootFolderRecoveryBlockedException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        Assert.Contains("waiting for restart recovery", exception.Blocker.PublicReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Guid.Empty, exception.Blocker.OperationId);
        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_AnonymousRegistrationPublicationUnderRoot_BlocksBeforeAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-anonymous-registration-recovery");
        await using var cleanup = fixture;
        await fixture.AddAnonymousRegistrationPublicationAsync();
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);

        var exception = await Assert.ThrowsAsync<RootFolderRecoveryBlockedException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        Assert.Contains("waiting for restart recovery", exception.Blocker.PublicReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Guid.Empty, exception.Blocker.OperationId);
        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [WindowsFact]
    public async Task ConfirmCurrentFolderAsync_ActiveRegistrationRecoveryWithDeviceAliasSourceUnderRoot_BlocksBeforeAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-registration-recovery-device-alias");
        await using var cleanup = fixture;
        await fixture.AddRegistrationRecoveryViaDeviceAliasSourceAsync();
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);

        var exception = await Assert.ThrowsAsync<RootFolderRecoveryBlockedException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        Assert.Contains("waiting for restart recovery", exception.Blocker.PublicReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Guid.Empty, exception.Blocker.OperationId);
        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [WindowsFact]
    public async Task ConfirmCurrentFolderAsync_ActiveRegistrationRecoveryWithForeignUnixPaths_DoesNotBlockUnrelatedRoot()
    {
        var fixture = await CreateFixtureAsync("confirm-registration-recovery-foreign-unix");
        await using var cleanup = fixture;
        await fixture.AddForeignRegistrationRecoveryAsync();
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);

        var confirmed = await fixture.Service.ConfirmCurrentFolderAsync(
            root.Id,
            root.Path,
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(root));

        Assert.Null(confirmed.DirectoryObjectIdentity);
        Assert.Equal(PathIdentityState.Valid, confirmed.PathIdentityState);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ActiveDeletionRecoveryWithLegacyFilePathUnderRoot_BlocksBeforeAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-delete-recovery-legacy-path");
        await using var cleanup = fixture;
        await fixture.AddDeletionRecoveryViaLegacyFilePathAsync();
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        Assert.Contains("deletion recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ReplacementBeforeCommit_RollsBackAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-replaced-before-commit");
        await using var cleanup = fixture;
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);
        fixture.Service.BeforeCommitForTest = fixture.ReplaceVisibleRoot;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentityVersion);
        Assert.Null(persisted.DirectoryObjectIdentity);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ReplacementAfterCommit_PreservesPathContractAndRecordsDiagnostic()
    {
        var fixture = await CreateFixtureAsync("confirm-replaced-after-commit");
        await using var cleanup = fixture;
        var root = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(root);
        fixture.Service.AfterCommitForTest = fixture.ReplaceVisibleRoot;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        var persisted = await fixture.LoadRootAsync();
        Assert.Null(persisted.DirectoryObjectIdentityVersion);
        Assert.Null(persisted.DirectoryObjectIdentity);
        Assert.Contains(
            "changed immediately after confirmation",
            persisted.DirectoryObjectIdentityUnavailableReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        var refreshed = await fixture.HealthResolver.ResolveAsync(persisted);
        Assert.Equal(RootFolderStorageState.Healthy, refreshed.State);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_PhysicalReplacement_PreservesChildPathOwnershipAndAllowsCreationBelowIt()
    {
        var fixture = await CreateFixtureAsync("confirm-replacement-ownership");
        await using var cleanup = fixture;
        var initialRoot = await fixture.ConfirmInitialGenerationAsync();
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var oldAuthorPath = Path.Join(initialRoot.Path, "Author");
        var oldOwnership = await fixture.CreateOwnedDirectoryAsync(oldAuthorPath);
        var oldOwnershipKey = Assert.IsType<string>(oldOwnership.PathOwnershipKey);

        fixture.ReplaceVisibleRoot();
        Directory.CreateDirectory(oldAuthorPath);
        var replacementSentinel = Path.Join(oldAuthorPath, "replacement.txt");
        await File.WriteAllTextAsync(replacementSentinel, "replacement generation");
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);
        Assert.Equal(RootFolderStorageState.Healthy, observation.State);

        await fixture.Service.ConfirmCurrentFolderAsync(
            changedRoot.Id,
            changedRoot.Path,
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(
                changedRoot));

        var persisted = await fixture.LoadOwnershipAsync(oldOwnership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persisted.State);
        Assert.Equal(oldOwnershipKey, persisted.PathOwnershipKey);
        Assert.Equal(changedRoot.Id, persisted.ManagedRootFolderId);
        var refreshed = await fixture.HealthResolver.ResolveAsync(await fixture.LoadRootAsync());
        Assert.Equal(RootFolderStorageState.Healthy, refreshed.State);
        Assert.True(refreshed.CanMutateFilesystem);

        var replacementResolution = await fixture.OwnershipStore.ResolveOwnedAsync(
            oldAuthorPath,
            semantics);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, replacementResolution.State);

        var newBookPath = Path.Join(oldAuthorPath, "New Book");
        var created = await fixture.OwnershipStore.EnsureCreatedHierarchyAsync(
            newBookPath,
            changedRoot.Path,
            semantics,
            "test");
        if (OperatingSystem.IsWindows())
        {
            var createdOwnership = Assert.Single(created);
            Assert.Equal(newBookPath, createdOwnership.CanonicalPath);
            Assert.Equal(LibraryDirectoryOwnershipState.Owned, createdOwnership.State);
            Assert.Equal(changedRoot.Id, createdOwnership.ManagedRootFolderId);
        }
        else
        {
            Assert.Empty(created);
            var createdResolution = await fixture.OwnershipStore.ResolveOwnedAsync(
                newBookPath,
                semantics);
            Assert.Equal(
                LibraryDirectoryOwnershipResolutionState.Unowned,
                createdResolution.State);
        }
        Assert.True(File.Exists(replacementSentinel));
        Assert.True(Directory.Exists(oldAuthorPath));
        Assert.True(Directory.Exists(newBookPath));
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_SameGeneration_PreservesExistingOwnershipAuthority()
    {
        var fixture = await CreateFixtureAsync("confirm-same-generation-ownership");
        await using var cleanup = fixture;
        var root = await fixture.ConfirmInitialGenerationAsync();
        var authorPath = Path.Join(root.Path, "Author");
        var ownership = await fixture.CreateOwnedDirectoryAsync(authorPath);
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        var token = await fixture.CreateCurrentGenerationConfirmationTokenAsync(root);

        await fixture.Service.ConfirmCurrentFolderAsync(
            root.Id,
            root.Path,
            token);

        var persisted = await fixture.LoadOwnershipAsync(ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persisted.State);
        Assert.Equal(ownershipKey, persisted.PathOwnershipKey);
        Assert.Equal(root.Id, persisted.ManagedRootFolderId);
        Assert.True(Directory.Exists(authorPath));
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ReplacementCommitFailure_RollsBackOwnershipRetirementAndRootAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-replacement-rollback");
        await using var cleanup = fixture;
        var initialRoot = await fixture.ConfirmInitialGenerationAsync();
        var initialRootIdentity = initialRoot.DirectoryObjectIdentity;
        var ownership = await fixture.CreateOwnedDirectoryAsync(
            Path.Join(initialRoot.Path, "Author"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        fixture.ReplaceVisibleRoot();
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);
        fixture.Service.BeforeCommitForTest = () =>
            throw new InvalidOperationException("Injected confirmation commit failure.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                changedRoot.Id,
                changedRoot.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(
                    changedRoot)));

        Assert.Contains("Injected", exception.Message);
        var persistedRoot = await fixture.LoadRootAsync();
        Assert.Equal(initialRootIdentity, persistedRoot.DirectoryObjectIdentity);
        var persistedOwnership = await fixture.LoadOwnershipAsync(ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persistedOwnership.State);
        Assert.Equal(ownershipKey, persistedOwnership.PathOwnershipKey);
        Assert.Equal(initialRoot.Id, persistedOwnership.ManagedRootFolderId);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ReplacementCancellationBeforeCommit_RollsBackOwnershipRetirementAndRootAuthorization()
    {
        var fixture = await CreateFixtureAsync("confirm-replacement-cancellation");
        await using var cleanup = fixture;
        var initialRoot = await fixture.ConfirmInitialGenerationAsync();
        var initialRootIdentity = initialRoot.DirectoryObjectIdentity;
        var ownership = await fixture.CreateOwnedDirectoryAsync(
            Path.Join(initialRoot.Path, "Author"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        fixture.ReplaceVisibleRoot();
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);
        using var cancellation = new CancellationTokenSource();
        fixture.Service.BeforeCommitForTest = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                changedRoot.Id,
                changedRoot.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(
                    changedRoot),
                cancellation.Token));

        var persistedRoot = await fixture.LoadRootAsync();
        Assert.Equal(initialRootIdentity, persistedRoot.DirectoryObjectIdentity);
        var persistedOwnership = await fixture.LoadOwnershipAsync(ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persistedOwnership.State);
        Assert.Equal(ownershipKey, persistedOwnership.PathOwnershipKey);
        Assert.Equal(initialRoot.Id, persistedOwnership.ManagedRootFolderId);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_PhysicalReplacementAlone_DoesNotInvalidatePathContractToken()
    {
        var fixture = await CreateFixtureAsync("confirm-replacement-stale-token-ownership");
        await using var cleanup = fixture;
        var root = await fixture.ConfirmInitialGenerationAsync();
        var ownership = await fixture.CreateOwnedDirectoryAsync(
            Path.Join(root.Path, "Author"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        fixture.ReplaceVisibleRoot();
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);
        fixture.ReplaceVisibleRoot();

        var confirmed = await fixture.Service.ConfirmCurrentFolderAsync(
            changedRoot.Id,
            changedRoot.Path,
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(root));

        Assert.Equal(PathIdentityState.Valid, confirmed.PathIdentityState);
        var persistedOwnership = await fixture.LoadOwnershipAsync(ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persistedOwnership.State);
        Assert.Equal(ownershipKey, persistedOwnership.PathOwnershipKey);
        Assert.Equal(root.Id, persistedOwnership.ManagedRootFolderId);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_PhysicalReplacement_PreservesPathOwnershipClaims()
    {
        var fixture = await CreateFixtureAsync("confirm-replacement-multiple-ownerships");
        await using var cleanup = fixture;
        var root = await fixture.ConfirmInitialGenerationAsync();
        var ownershipIds = new[]
        {
            (await fixture.CreateOwnedDirectoryAsync(
                Path.Join(root.Path, "Author"))).Id,
            (await fixture.CreateOwnedDirectoryAsync(
                Path.Join(root.Path, "Author", "Series"))).Id,
            (await fixture.CreateOwnedDirectoryAsync(
                Path.Join(root.Path, "Another Author"))).Id
        };
        fixture.ReplaceVisibleRoot();
        Directory.CreateDirectory(Path.Join(root.Path, "Author"));
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);

        await fixture.Service.ConfirmCurrentFolderAsync(
            changedRoot.Id,
            changedRoot.Path,
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(root));

        foreach (var ownershipId in ownershipIds)
        {
            var persisted = await fixture.LoadOwnershipAsync(ownershipId);
            Assert.Equal(LibraryDirectoryOwnershipState.Owned, persisted.State);
            Assert.False(string.IsNullOrWhiteSpace(persisted.PathOwnershipKey));
            Assert.Equal(root.Id, persisted.ManagedRootFolderId);
        }
    }

    [Theory]
    [InlineData(LibraryDirectoryOwnershipState.Retained)]
    [InlineData(LibraryDirectoryOwnershipState.Removing)]
    [InlineData(LibraryDirectoryOwnershipState.Conflict)]
    [InlineData(LibraryDirectoryOwnershipState.Unavailable)]
    public async Task ConfirmCurrentFolderAsync_PhysicalReplacement_PreservesNonTerminalOwnershipState(
        LibraryDirectoryOwnershipState state)
    {
        var fixture = await CreateFixtureAsync($"confirm-replacement-state-{state}");
        await using var cleanup = fixture;
        var root = await fixture.ConfirmInitialGenerationAsync();
        var ownership = await fixture.CreateOwnedDirectoryAsync(
            Path.Join(root.Path, "Author"));
        await fixture.UpdateOwnershipAsync(ownership.Id, persisted =>
        {
            persisted.State = state;
            if (state == LibraryDirectoryOwnershipState.Conflict)
            {
                persisted.PathOwnershipKey = null;
            }
        });
        fixture.ReplaceVisibleRoot();
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);

        await fixture.Service.ConfirmCurrentFolderAsync(
            changedRoot.Id,
            changedRoot.Path,
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(root));

        var persisted = await fixture.LoadOwnershipAsync(ownership.Id);
        Assert.Equal(state, persisted.State);
        if (state == LibraryDirectoryOwnershipState.Conflict)
        {
            Assert.Null(persisted.PathOwnershipKey);
        }
        else
        {
            Assert.Equal(ownership.PathOwnershipKey, persisted.PathOwnershipKey);
        }
        Assert.Equal(root.Id, persisted.ManagedRootFolderId);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_IncompleteOwnershipPathMigration_BlocksReplacementConfirmation()
    {
        var fixture = await CreateFixtureAsync("confirm-replacement-incomplete-ownership-migration");
        await using var cleanup = fixture;
        var root = await fixture.ConfirmInitialGenerationAsync();
        var ownership = await fixture.CreateOwnedDirectoryAsync(
            Path.Join(root.Path, "Author"));
        await fixture.AddIncompleteOwnershipPathMigrationAsync(root, ownership);
        fixture.ReplaceVisibleRoot();
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ConfirmCurrentFolderAsync(
                changedRoot.Id,
                changedRoot.Path,
                await fixture.CreateCurrentGenerationConfirmationTokenAsync(root)));

        Assert.Contains("ownership path migration recovery is incomplete", exception.Message);
        var persistedRoot = await fixture.LoadRootAsync();
        Assert.Equal(root.DirectoryObjectIdentity, persistedRoot.DirectoryObjectIdentity);
        var persistedOwnership = await fixture.LoadOwnershipAsync(ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persistedOwnership.State);
        Assert.Equal(root.Id, persistedOwnership.ManagedRootFolderId);
    }

    [Fact]
    public async Task ConfirmCurrentFolderAsync_ReplacementGeneration_DoesNotRetireOwnershipFromPersistedIdentity()
    {
        var fixture = await CreateFixtureAsync("confirm-replacement-restart");
        await using var cleanup = fixture;
        var root = await fixture.ConfirmInitialGenerationAsync();
        var authorPath = Path.Join(root.Path, "Author");
        var ownership = await fixture.CreateOwnedDirectoryAsync(authorPath);
        fixture.ReplaceVisibleRoot();
        Directory.CreateDirectory(authorPath);
        var changedRoot = await fixture.LoadRootAsync();
        var observation = await fixture.HealthResolver.ResolveAsync(changedRoot);
        await fixture.Service.ConfirmCurrentFolderAsync(
            changedRoot.Id,
            changedRoot.Path,
            await fixture.CreateCurrentGenerationConfirmationTokenAsync(root));

        var recreatedStore = fixture.CreateOwnershipStore();
        var replacementResolution = await recreatedStore.ResolveOwnedAsync(
            authorPath,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Owned,
            replacementResolution.State);
        var persistedOwnership = await fixture.LoadOwnershipAsync(ownership.Id);
        Assert.Equal(
            LibraryDirectoryOwnershipState.Owned,
            persistedOwnership.State);

        var childPath = Path.Join(authorPath, "After Restart");
        var created = await recreatedStore.EnsureCreatedHierarchyAsync(
            childPath,
            root.Path,
            FileSystemPathSemantics.CurrentHostDefault,
            "test");
        if (OperatingSystem.IsWindows())
        {
            var createdOwnership = Assert.Single(created);
            Assert.Equal(childPath, createdOwnership.CanonicalPath);
            Assert.Equal(LibraryDirectoryOwnershipState.Owned, createdOwnership.State);
        }
        else
        {
            Assert.Empty(created);
            var resolution = await recreatedStore.ResolveOwnedAsync(
                childPath,
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(
                LibraryDirectoryOwnershipResolutionState.Unowned,
                resolution.State);
        }
        Assert.True(Directory.Exists(childPath));
    }

    private async Task<ConfirmationFixture> CreateFixtureAsync(
        string name,
        Func<string, IReadOnlyList<MoveJob>>? blockingJobsFactory = null)
    {
        var parent = FileService.GetTempDirectory(name);
        var rootPath = Path.Join(parent, "library");
        Directory.CreateDirectory(rootPath);
        var dbPath = Path.Join(parent, "listenarr.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = rootPath,
                CaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    rootPath,
                    semantics),
                DirectoryObjectIdentityUnavailableReason =
                    "The root folder physical directory has not been confirmed."
            });
            await setup.SaveChangesAsync();
        }

        var dbFactory = new TestDbFactory(options);
        var moveQueue = new Mock<IMoveQueueService>(MockBehavior.Strict);
        moveQueue
            .Setup(queue => queue.GetFilesystemBlockingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => blockingJobsFactory?.Invoke(rootPath) ?? []);
        var mutationCoordinator = new FilesystemMutationCoordinator();
        var audiobookCoordinator = new AudiobookOperationCoordinator();
        var identityResolver = new DirectoryObjectIdentityResolver();
        var recoveryProbe = new FileRegistrationRecoveryProbe(dbFactory);
        var service = new RootFolderStorageConfirmationService(
            dbFactory,
            new FileSystemSemanticsResolver(),
            moveQueue.Object,
            mutationCoordinator,
            audiobookCoordinator,
            recoveryProbe);
        var healthResolver = new RootFolderStorageHealthResolver(identityResolver);
        var ownershipStore = new EfLibraryDirectoryOwnershipStore(
            dbFactory,
            TimeProvider.System);
        return new ConfirmationFixture(
            parent,
            rootPath,
            dbFactory,
            service,
            healthResolver,
            ownershipStore,
            mutationCoordinator,
            audiobookCoordinator);
    }

    private sealed class ConfirmationFixture(
        string parentPath,
        string rootPath,
        TestDbFactory dbFactory,
        RootFolderStorageConfirmationService service,
        RootFolderStorageHealthResolver healthResolver,
        EfLibraryDirectoryOwnershipStore ownershipStore,
        FilesystemMutationCoordinator mutationCoordinator,
        AudiobookOperationCoordinator audiobookCoordinator)
        : IAsyncDisposable
    {
        private int _replacementCount;

        public RootFolderStorageConfirmationService Service { get; } = service;
        public RootFolderStorageHealthResolver HealthResolver { get; } = healthResolver;
        public EfLibraryDirectoryOwnershipStore OwnershipStore { get; } = ownershipStore;

        public async Task<RootFolder> ConfirmInitialGenerationAsync()
        {
            var root = await LoadRootAsync();
            return await ConfirmCurrentAsync(root);
        }

        public async Task<RootFolder> ConfirmCurrentAsync(
            RootFolder root,
            CancellationToken cancellationToken = default)
        {
            var token = await CreateCurrentGenerationConfirmationTokenAsync(root);
            return await Service.ConfirmCurrentFolderAsync(
                root.Id,
                root.Path,
                token,
                cancellationToken);
        }

        public async Task<string> CreateCurrentGenerationConfirmationTokenAsync(RootFolder root)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalPath,
                    out var reason))
            {
                throw new InvalidOperationException(reason);
            }

            var semantics = await new FileSystemSemanticsResolver().ResolveAsync(
                canonicalPath,
                root.CaseSensitivityMode);
            if (semantics.State != PathIdentityState.Valid)
            {
                throw new InvalidOperationException(
                    semantics.Reason
                        ?? "The fixture root filesystem semantics could not be resolved.");
            }

            return RootFolderStorageHealthResolver.CreateConfirmationToken(
                root,
                canonicalPath,
                semantics.Semantics);
        }

        public EfLibraryDirectoryOwnershipStore CreateOwnershipStore() =>
            new(dbFactory, TimeProvider.System);

        public async Task AddVerifiedOrganizeRecoveryAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
            {
                OperationId = Guid.NewGuid(),
                BatchId = Guid.NewGuid(),
                AudiobookId = 42,
                SourcePath = Path.Join(rootPath, "Author", "Book", "book.m4b"),
                DestinationPath = Path.Join(rootPath, "Author", "Book", "organized.m4b"),
                State = VerifiedFileRenameState.NeedsAttention
            });
            await db.SaveChangesAsync();
        }

        public async Task AddRegistrationRecoveryAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Audiobooks.Add(new Audiobook
            {
                Id = 42,
                Title = "Pending Registration Recovery",
                BasePath = Path.Join(rootPath, "Author", "Book")
            });
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                Action = FileAction.Move,
                SourcePath = Path.Join(rootPath, "incoming", "book.m4b"),
                DestinationPath = Path.Join(rootPath, "Author", "Book", "book.m4b"),
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = 1,
                State = FileMutationJournalState.SourceDeletionAuthorized,
                AudiobookId = 42,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        public async Task AddAnonymousRegistrationPublicationAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Copy,
                SourcePath = Path.Join(parentPath, "incoming-anonymous", "book.m4b"),
                DestinationPath = Path.Join(rootPath, "Author", "Book", "book.m4b"),
                SourceParentDirectoryObjectIdentity = "source-parent",
                DestinationParentDirectoryObjectIdentity = "destination-parent",
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = 5,
                State = FileMutationJournalState.TargetVerified,
                AudiobookId = null,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        public async Task AddRegistrationRecoveryViaDeviceAliasSourceAsync()
        {
            var sourceDirectory = Path.Join(rootPath, "incoming-device-alias");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Join(sourceDirectory, "book.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio");
            var outsideBasePath = Path.Join(parentPath, "outside", "Book");
            Directory.CreateDirectory(outsideBasePath);

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Audiobooks.Add(new Audiobook
            {
                Id = 44,
                Title = "Pending Device Alias Registration Recovery",
                BasePath = outsideBasePath
            });
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                Action = FileAction.Move,
                SourcePath = @"\\?\" + sourcePath,
                DestinationPath = Path.Join(outsideBasePath, "book.m4b"),
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = 5,
                State = FileMutationJournalState.SourceDeletionAuthorized,
                AudiobookId = 44,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        public async Task AddForeignRegistrationRecoveryAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Audiobooks.Add(new Audiobook
            {
                Id = 45,
                Title = "Pending Foreign Registration Recovery",
                BasePath = "/foreign/library/Book"
            });
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                Action = FileAction.Move,
                SourcePath = "/foreign/downloads/book.m4b",
                DestinationPath = "/foreign/library/Book/book.m4b",
                SourcePhysicalObjectIdentity = "foreign-source-generation",
                TargetPhysicalObjectIdentity = "foreign-target-generation",
                SourceLength = 5,
                State = FileMutationJournalState.SourceDeletionAuthorized,
                AudiobookId = 45,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        public async Task AddDeletionRecoveryViaLegacyFilePathAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Audiobooks.Add(new Audiobook
            {
                Id = 43,
                Title = "Pending Legacy Deletion Recovery",
                BasePath = null,
                FilePath = Path.Join(rootPath, "Legacy", "book.m4b")
            });
            db.AudiobookDeletionIntents.Add(new AudiobookDeletionIntent
            {
                Id = Guid.NewGuid(),
                AudiobookId = 43,
                DeleteFolder = true,
                State = AudiobookDeletionIntentState.Planned
            });
            await db.SaveChangesAsync();
        }

        public async Task<LibraryDirectoryOwnership> CreateOwnedDirectoryAsync(
            string path)
        {
            Directory.CreateDirectory(path);
            return await OwnershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    path,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test-fixture",
                    Guid.NewGuid()));
        }

        public async Task<RootFolder> LoadRootAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.RootFolders.AsNoTracking().SingleAsync();
        }

        public async Task UpdateRootAsync(Action<RootFolder> update)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var root = await db.RootFolders.SingleAsync();
            update(root);
            await db.SaveChangesAsync();
        }

        public async Task<LibraryDirectoryOwnership> LoadOwnershipAsync(long ownershipId)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.LibraryDirectoryOwnerships
                .AsNoTracking()
                .SingleAsync(ownership => ownership.Id == ownershipId);
        }

        public async Task UpdateOwnershipAsync(
            long ownershipId,
            Action<LibraryDirectoryOwnership> update)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ownership = await db.LibraryDirectoryOwnerships
                .SingleAsync(candidate => candidate.Id == ownershipId);
            update(ownership);
            await db.SaveChangesAsync();
        }

        public async Task AddIncompleteOwnershipPathMigrationAsync(
            RootFolder root,
            LibraryDirectoryOwnership ownership)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var semantics = ownership.GetIdentity().Semantics;
            var targetPath = Path.Join(parentPath, "migration-target", "Author");
            var relocation = new RootFolderRelocation
            {
                Id = Guid.NewGuid(),
                RootFolderId = root.Id,
                ActiveRootFolderId = null,
                SourcePath = root.Path,
                SourceCaseSensitivityMode = root.CaseSensitivityMode,
                TargetPath = Path.GetDirectoryName(targetPath)!,
                Mode = RootFolderRelocationMode.MetadataOnly,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TargetCaseSensitivityMode = root.CaseSensitivityMode,
                TargetIdentityEnrollmentState = TargetIdentityEnrollmentState.NotRequired
            };
            db.RootFolderRelocations.Add(relocation);
            db.LibraryDirectoryOwnershipPathMigrations.Add(
                new LibraryDirectoryOwnershipPathMigration
                {
                    OwnershipId = ownership.Id,
                    RelocationId = relocation.Id,
                    SourceCanonicalPath = ownership.CanonicalPath,
                    SourcePathSyntax = ownership.PathSyntax,
                    SourceCaseSensitivity = ownership.PathCaseSensitivity,
                    SourceCaseSensitivityMode = ownership.PathCaseSensitivityMode,
                    SourceIdentityBoundary = ownership.PathIdentityBoundary,
                    SourceIdentityLookupKey = ownership.PathIdentityLookupKey,
                    SourceOwnershipKey = ownership.PathOwnershipKey
                        ?? throw new InvalidOperationException(
                            "The ownership fixture has no source ownership key."),
                    TargetCanonicalPath = targetPath,
                    TargetPathSyntax = semantics.Syntax,
                    TargetCaseSensitivity = semantics.CaseSensitivity,
                    TargetCaseSensitivityMode = ownership.PathCaseSensitivityMode,
                    TargetIdentityBoundary = targetPath,
                    TargetIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                        "library-directory",
                        targetPath,
                        semantics.Syntax),
                    TargetOwnershipKey = FileSystemPathIdentity.CreateKey(
                        "library-directory",
                        targetPath,
                        semantics)
                });
            await db.SaveChangesAsync();
        }

        public void ReplaceVisibleRoot()
        {
            _replacementCount++;
            var replacedPath = Path.Join(parentPath, $"original-{_replacementCount}");
            Directory.Move(rootPath, replacedPath);
            Directory.CreateDirectory(rootPath);
        }

        public ValueTask DisposeAsync()
        {
            audiobookCoordinator.Dispose();
            mutationCoordinator.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListenArrDbContext(options));
    }
}
