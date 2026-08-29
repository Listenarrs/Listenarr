/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_ScanPathValidationTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_ScanPathValidationTests : BaseTests
    {
        [Fact]
        public void GetScanJobStatus_ReturnsPublicContractWithoutPathAuthorityOrInternalError()
        {
            var jobId = Guid.NewGuid();
            var pathIdentity = new PathIdentitySnapshot(
                FileSystemPathSyntax.Windows,
                FileSystemCaseSensitivity.Insensitive,
                FileSystemCaseSensitivityMode.Auto,
                "C:\\private\\library");
            var physicalIdentity = new ScanPathPhysicalIdentity(
                "boundary-secret",
                "scan-root-secret");
            var job = new ScanJob
            {
                Id = jobId,
                AudiobookId = 42,
                Path = "C:\\private\\library\\book",
                PathIdentity = pathIdentity,
                PhysicalIdentity = physicalIdentity,
                Status = "Failed",
                Error = "C:\\private\\library\\book could not be opened by worker secret",
                CorrelationId = "correlation-secret",
                DownloadId = "download-secret",
                AuthorizationMode = ScanAuthorizationMode.PreauthorizedPath,
                EnqueuedAt = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc)
            };
            ScanJob? queuedJob = job;
            var queue = new Mock<IScanQueueService>(MockBehavior.Strict);
            queue.Setup(service => service.TryGetJob(jobId, out queuedJob))
                .Returns(true);
            Init(services => services.WithSingleton(queue.Object));

            var result = _provider.GetRequiredService<LibraryController>()
                .GetScanJobStatus(jobId.ToString("D"));

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.IsNotType<ScanJob>(ok.Value);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains("The scan failed", json, StringComparison.Ordinal);
            foreach (var forbidden in new[]
            {
                job.Path,
                pathIdentity.BoundaryPath,
                physicalIdentity.BoundaryObjectIdentity,
                physicalIdentity.ScanRootObjectIdentity,
                job.CorrelationId,
                job.DownloadId,
                "worker secret",
                nameof(ScanJob.PathIdentity),
                nameof(ScanJob.PhysicalIdentity),
                nameof(ScanJob.AuthorizationMode)
            })
            {
                Assert.DoesNotContain(forbidden!, json, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task ScanAudiobook_PreCanceledRequest_DoesNotPlanOrEnqueueScan()
        {
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Canceled Scan")
                    .WithBasePath(FileService.GetTempDirectory("canceled-scan"))
                    .Build());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var scan = _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(
                    audiobook.Id,
                    request: null,
                    cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        }

        [Fact]
        public async Task ScanAudiobook_UnresolvedMoveExecution_BlocksBeforeScanPlanning()
        {
            var source = FileService.GetTempDirectory("scan-unresolved-move-source");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"scan-unresolved-move-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Unresolved Move Scan Fence")
                .WithBasePath(source)
                .Build());
            await MoveJobTestFactory.SeedUnresolvedExecutionAsync(
                _provider,
                audiobook.Id,
                source,
                target);

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(
                    audiobook.Id,
                    new LibraryController.ScanRequest { Path = source });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            var payload = JsonSerializer.Serialize(conflict.Value);
            Assert.Contains("move_recovery_required", payload, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ScanAudiobook_PathAuthorizationFailure_DoesNotExposeInternalReason()
        {
            const string secret = "C:\\private\\identity-secret";
            var authorization = new Mock<IScanPathAuthorizationService>(
                MockBehavior.Strict);
            authorization.Setup(service => service.AuthorizeAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(ScanPathAuthorizationResult.Rejected(
                    ScanPathAuthorizationFailure.IdentityUnavailable,
                    secret));
            Init(services => services
                .Without<IScanQueueService>()
                .WithSingleton(authorization.Object));
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder().WithTitle("Secret Scan").Build());

            var result = await _provider
                .GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(
                    audiobook.Id,
                    new LibraryController.ScanRequest
                    {
                        Path = Path.GetTempPath()
                    });

            var conflict = Assert.IsType<ObjectResult>(result);
            Assert.Equal(409, conflict.StatusCode);
            var json = JsonSerializer.Serialize(conflict.Value);
            Assert.Contains(
                "Scan path identity could not be established safely",
                json,
                StringComparison.Ordinal);
            Assert.Contains(
                nameof(ScanPathAuthorizationFailure.IdentityUnavailable),
                json,
                StringComparison.Ordinal);
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "AllowsRequestPathWithinConfiguredRoot_ReturnsOk")]
        public async Task ScanAudiobook_AllowsRequestPathWithinConfiguredRoot_ReturnsOk()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-test-root");
            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(tempRoot);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(FileService.GetTempPath()).Build());
            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").Build());
            var result = await controller.ScanAudiobookFiles(ab.Id, new LibraryController.ScanRequest { Path = tempRoot });
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            Assert.Contains("Scan complete", ok.Value?.ToString() ?? string.Empty);
            Assert.Contains("found = 0", ok.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        public async Task ScanAudiobook_QueuedScanPersistsConfiguredRootIdentity()
        {
            var configuredRoot = FileService.GetTempDirectory(
                "listenarr-queued-identity-root");
            var requestedPath = Path.Join(configuredRoot, "Author", "Book");
            Directory.CreateDirectory(requestedPath);
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(configuredRoot);
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(configuredRoot)
                    .Build());
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Book")
                    .Build());

            var result = await controller.ScanAudiobookFiles(
                audiobook.Id,
                new LibraryController.ScanRequest { Path = requestedPath });

            Assert.IsType<AcceptedResult>(result);
            var queue = Assert.IsType<ScanQueueService>(
                _provider.GetRequiredService<IScanQueueService>());
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(requestedPath, job.Path);
            Assert.True(job.PathIdentity.HasValue);
            Assert.True(FileSystemPathIdentity.AreEquivalent(
                configuredRoot,
                job.PathIdentity.Value.BoundaryPath,
                job.PathIdentity.Value.Semantics));
        }

        [Theory]
        [InlineData("same", true)]
        [InlineData("ancestor", true)]
        [InlineData("descendant", false)]
        [InlineData("sibling", false)]
        public async Task ScanAudiobook_AuthoritativeScope_RequiresScanRootToCoverExistingBasePath(
            string relationship,
            bool expectedAuthoritative)
        {
            var configuredRoot = FileService.GetTempDirectory(
                $"listenarr-scan-authority-{relationship}");
            var existingBasePath = Path.Join(configuredRoot, "Author", "Book");
            Directory.CreateDirectory(existingBasePath);
            var requestedPath = relationship switch
            {
                "same" => existingBasePath,
                "ancestor" => Path.Join(configuredRoot, "Author"),
                "descendant" => Path.Join(existingBasePath, "Disc 1"),
                "sibling" => Path.Join(configuredRoot, "Other Author", "Other Book"),
                _ => throw new InvalidOperationException(
                    $"Unknown relationship fixture: {relationship}")
            };
            Directory.CreateDirectory(requestedPath);
            await AddAuthorizedRootAsync(configuredRoot);
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(configuredRoot)
                    .Build());
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Authority Book")
                    .WithBasePath(existingBasePath)
                    .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(
                    audiobook.Id,
                    new LibraryController.ScanRequest { Path = requestedPath });

            Assert.IsType<AcceptedResult>(result);
            var queue = Assert.IsType<ScanQueueService>(
                _provider.GetRequiredService<IScanQueueService>());
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(expectedAuthoritative, job.IsAuthoritativeScope);
        }

        [Fact]
        public async Task ScanAudiobook_PersistsBasePathBeforeClaimingRelativeFileOwnership()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-scan-basepath-order");
            var audioPath = await FileService.GetFileAsync(tempRoot, "Test.m4b");
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            Init(services => services.Without<IScanQueueService>().WithSingleton<IAudiobookFileService>(fileService.Object));
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(tempRoot);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(tempRoot).Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").Build());
            fileService.Setup(service => service.EnsureAudiobookFileAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    "Manual Scan",
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, IAudiobookFileRegistrationLease, string?, CancellationToken>(async (claimedAudiobook, lease, _, token) =>
                {
                    Assert.Equal(Path.GetFullPath(audioPath), Path.GetFullPath(lease.PublicPath));
                    Assert.True(lease.MatchesCurrentPublication());
                    var persisted = await _audiobookRepository.GetByIdSnapshotAsync(claimedAudiobook.Id, token);
                    Assert.NotNull(persisted);
                    Assert.Equal(Path.GetFullPath(tempRoot), Path.GetFullPath(persisted!.BasePath!));
                    return false;
                });
            var result = await controller.ScanAudiobookFiles(audiobook.Id, new LibraryController.ScanRequest { Path = tempRoot });
            Assert.IsType<OkObjectResult>(result);
            fileService.Verify(service => service.EnsureAudiobookFileAsync(
                It.IsAny<Audiobook>(),
                It.Is<IAudiobookFileRegistrationLease>(lease =>
                    lease.PublicPath == audioPath),
                "Manual Scan",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ScanAudiobook_ExistingAbsoluteOwnershipRow_IsNotRemovedAsMissing()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-scan-absolute-row");
            var audioPath = await FileService.GetFileAsync(tempRoot, "Test.m4b");
            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(tempRoot);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(tempRoot).Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").WithBasePath(tempRoot).Build());
            var resolution = await _provider.GetRequiredService<IFileSystemSemanticsResolver>().ResolveAsync(tempRoot);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            var identity = AudiobookFilePathIdentity.CreateValid(audioPath, resolution.Semantics, FileSystemCaseSensitivityMode.Auto, resolution.BoundaryPath);
            var existingFile = AudiobookFile.CreateUnresolved(audioPath);
            existingFile.AudiobookId = audiobook.Id;
            existingFile.ApplyPathIdentity(audioPath, identity);
            await _audiobookFileRepository.AddAsync(existingFile);
            var result = await controller.ScanAudiobookFiles(audiobook.Id, new LibraryController.ScanRequest { Path = tempRoot });
            Assert.IsType<OkObjectResult>(result);
            var retained = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
            Assert.Equal(existingFile.Id, retained.Id);
            Assert.Equal(audioPath, retained.Path);
        }

        [DirectoryLinkFact]
        public async Task ScanAudiobook_SymlinkedDirectoryOutsideRoot_IsNotTraversed()
        {

            var tempRoot = FileService.GetTempDirectory("listenarr-scan-link-root");
            var outsideRoot = FileService.GetTempDirectory("listenarr-scan-link-outside");
            var outsideFile = await FileService.GetFileAsync(outsideRoot, "Test.m4b", "audio");
            var linkedDirectory = Path.Join(tempRoot, "linked");
            Directory.CreateSymbolicLink(linkedDirectory, outsideRoot);
            Assert.True(File.Exists(Path.Join(linkedDirectory, Path.GetFileName(outsideFile))));

            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(tempRoot);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .Build());

            var result = await controller.ScanAudiobookFiles(
                audiobook.Id,
                new LibraryController.ScanRequest { Path = tempRoot });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Scan complete", ok.Value?.ToString() ?? string.Empty);
            Assert.Contains("complete = False", ok.Value?.ToString() ?? string.Empty);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "FilesystemRootCannotAuthorizeScan")]
        public async Task ScanAudiobook_FilesystemRootCannotAuthorizeScan()
        {
            var requestedPath = FileService.GetTempDirectory(
                "listenarr-scan-root-boundary");
            var filesystemRoot = Path.GetPathRoot(requestedPath);
            Assert.False(string.IsNullOrWhiteSpace(filesystemRoot));
            var controller = _provider.GetRequiredService<LibraryController>();
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(filesystemRoot!)
                    .Build());
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Test")
                    .Build());

            var result = await controller.ScanAudiobookFiles(
                audiobook.Id,
                new LibraryController.ScanRequest { Path = requestedPath });

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(
                "No root folders configured",
                bad.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "QueuedScanRejectsRequestPathOutsideConfiguredRoots")]
        public async Task ScanAudiobook_QueuedScanRejectsRequestPathOutsideConfiguredRoots()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-queued-scan-root");
            var outside = FileService.GetTempDirectory("listenarr-queued-scan-outside");
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(tempRoot, "root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .Build());

            var result = await controller.ScanAudiobookFiles(
                audiobook.Id,
                new LibraryController.ScanRequest { Path = outside });

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("not within configured root folders", bad.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "StoredBasePathOutsideConfiguredRootsIsRejected")]
        public async Task ScanAudiobook_StoredBasePathOutsideConfiguredRootsIsRejected()
        {
            var configuredRoot = FileService.GetTempDirectory(
                "listenarr-scan-configured-root");
            var outsideBasePath = FileService.GetTempDirectory(
                "listenarr-scan-outside-base");
            var controller = _provider.GetRequiredService<LibraryController>();
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(configuredRoot)
                    .Build());
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Test")
                    .WithBasePath(outsideBasePath)
                    .Build());

            var result = await controller.ScanAudiobookFiles(
                audiobook.Id,
                request: null);

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, bad.StatusCode);
            Assert.Contains(
                "BasePath is not within configured root folders",
                bad.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "ExplicitOutsidePathIsRejectedEvenWhenBasePathExists")]
        public async Task ScanAudiobook_ExplicitOutsidePathIsRejectedEvenWhenBasePathExists()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-scan-existing-base");
            var bookRoot = FileService.GetTempDirectory("listenarr-scan-existing-book");
            var outside = FileService.GetTempDirectory("listenarr-scan-existing-outside");
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(tempRoot, "root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(bookRoot)
                .Build());

            var result = await controller.ScanAudiobookFiles(
                audiobook.Id,
                new LibraryController.ScanRequest { Path = outside });

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("not within configured root folders", bad.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "RejectsRequestPathOutsideConfiguredRoots_ReturnsBadRequest")]
        public async Task ScanAudiobook_RejectsRequestPathOutsideConfiguredRoots_ReturnsBadRequest()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-test-root");
            var other = FileService.GetTempDirectory("listenarr-other");
            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await AddAuthorizedRootAsync(tempRoot, "root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(Path.Join(FileService.GetTempPath(), "different-root")).Build());
            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").Build());
            var result = await controller.ScanAudiobookFiles(ab.Id, new LibraryController.ScanRequest { Path = other });
            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, bad.StatusCode);
            Assert.Contains("not within configured root folders", bad.Value?.ToString() ?? string.Empty);
        }
    }
}
