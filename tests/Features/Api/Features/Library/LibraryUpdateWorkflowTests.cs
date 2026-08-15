/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Name", "LibraryUpdateWorkflowTests")]
[Trait("Area", "LibraryApi")]
[Trait("Category", "LibraryController")]
public sealed class LibraryUpdateWorkflowTests : BaseTests
{
    [WindowsFact]
    public async Task UpdateAsync_ForeignPersistedBasePathAlias_RoutesThroughAuthoritativeRewrite()
    {
        var id = 41;
        var nativeTarget = Path.Join(
            Path.GetPathRoot(Environment.CurrentDirectory)!,
            "listenarr-update-foreign-alias",
            Guid.NewGuid().ToString("N"));
        var foreignSource = TempFileService
            .GetWindowsRootRelativeForeignAlias(nativeTarget);
        var before = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = foreignSource
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = nativeTarget
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                nativeTarget,
                foreignSource,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookDestinationRewriteResult(
                id,
                nativeTarget,
                foreignSource));

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest { BasePath = nativeTarget });

        Assert.IsType<OkObjectResult>(result);
        rewriteService.Verify(candidate => candidate.RewriteDestinationAsync(
            id,
            nativeTarget,
            foreignSource,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_DestinationOnlyRewrite_DoesNotIssueMetadataWrite()
    {
        var id = 42;
        var source = Path.Join(Path.GetTempPath(), $"listenarr-update-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"listenarr-update-target-{Guid.NewGuid():N}");
        var before = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = source,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = target,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                target,
                source,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookDestinationRewriteResult(id, target, source));

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(id, new AudiobookUpdateRequest { BasePath = target });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(candidate => candidate.UpdateAsync(It.IsAny<Audiobook>()), Times.Never);
        Assert.True(after.Explicit);
        Assert.True(after.Abridged);
        Assert.False(after.Monitored);
    }

    [Fact]
    public async Task UpdateAsync_CancelledAfterDestinationCommit_CompletesRequestedMetadataFinalization()
    {
        var id = 44;
        var source = Path.Join(Path.GetTempPath(), $"listenarr-update-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"listenarr-update-target-{Guid.NewGuid():N}");
        var before = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = source
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = target
        };
        using var cancellation = new CancellationTokenSource();
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        repository.Setup(candidate => candidate.UpdateAsync(after)).ReturnsAsync(true);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                target,
                source,
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(
                    new AudiobookDestinationRewriteResult(id, target, source));
            });

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest
            {
                BasePath = target,
                Title = "Edited"
            },
            cancellation.Token);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Edited", after.Title);
        repository.Verify(candidate => candidate.UpdateAsync(after), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_DestinationAndMetadataUpdate_PreservesOmittedBooleans()
    {
        var id = 43;
        var source = Path.Join(Path.GetTempPath(), $"listenarr-update-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"listenarr-update-target-{Guid.NewGuid():N}");
        var before = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = source,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = target,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        repository.Setup(candidate => candidate.UpdateAsync(after)).ReturnsAsync(true);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                target,
                source,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookDestinationRewriteResult(id, target, source));

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest
            {
                BasePath = target,
                Title = "Edited"
            });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Edited", after.Title);
        Assert.True(after.Explicit);
        Assert.True(after.Abridged);
        Assert.False(after.Monitored);
        repository.Verify(candidate => candidate.UpdateAsync(after), Times.Once);
    }
}
