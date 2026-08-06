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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_UpdateAudiobookTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_UpdateAudiobookTests : BaseTests
    {
        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "PersistsExpandedMetadataFields")]
        public async Task UpdateAudiobook_PersistsExpandedMetadataFields()
        {
            var existingAudiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Original Title",
                Subtitle = "Original Subtitle",
                Authors = new List<string> { "Original Author" },
                Narrators = new List<string> { "Original Narrator" },
                Description = "Original description",
                Publisher = "Original Publisher",
                Language = "english",
                PublishedDate = "2024-01-01",
                PublishYear = "2024",
                Runtime = 600,
                Edition = "Original Edition",
                Version = "Original Version",
                Series = "Original Series",
                SeriesNumber = "1",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Original Series",
                        SeriesNumber = "1",
                        IsPrimary = true,
                        SortOrder = 0
                    }
                },
                Genres = new List<string> { "Fantasy" },
                ImageUrl = "https://example.com/original.jpg",
                Tags = new List<string> { "tag-one" },
                Monitored = true,
                Explicit = false,
                Abridged = false,
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new AudiobookUpdateRequest
            {
                Title = "Edited Title",
                Subtitle = "Edited Subtitle",
                Authors = new List<string> { "Edited Author" },
                Narrators = new List<string> { "Edited Narrator" },
                Description = "Edited description",
                Publisher = "Edited Publisher",
                Language = "swedish",
                PublishedDate = "2025-02-01",
                PublishYear = "2025",
                Runtime = 720,
                Edition = "Collector Edition",
                Version = "Edited Version",
                Series = "Edited Universe",
                SeriesNumber = "4",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Edited Universe",
                        SeriesNumber = "4",
                        IsPrimary = true,
                        SortOrder = 0
                    },
                    new()
                    {
                        SeriesName = "Anthology Line",
                        SeriesNumber = "12",
                        IsPrimary = false,
                        SortOrder = 1
                    }
                },
                Genres = new List<string> { "Sci-Fi", "Adventure" },
                ImageUrl = "https://example.com/edited.jpg",
                Tags = new List<string> { "tag-two" },
                Monitored = false,
                Explicit = true,
                Abridged = true,
            };

            var actionResult = await controller.UpdateAudiobook(existingAudiobook.Id, request);

            Assert.IsType<OkObjectResult>(actionResult);
            var storedAudiobook = await GetFreshAudiobookAsync(existingAudiobook.Id);
            Assert.NotNull(storedAudiobook);
            Assert.Equal("Edited Title", storedAudiobook.Title);
            Assert.Equal("Edited Subtitle", storedAudiobook.Subtitle);
            Assert.Equal(new List<string> { "Edited Author" }, storedAudiobook.Authors);
            Assert.Equal(new List<string> { "Edited Narrator" }, storedAudiobook.Narrators);
            Assert.Equal("Edited description", storedAudiobook.Description);
            Assert.Equal("Edited Publisher", storedAudiobook.Publisher);
            Assert.Equal("swedish", storedAudiobook.Language);
            Assert.Equal("2025-02-01", storedAudiobook.PublishedDate);
            Assert.Equal("2025", storedAudiobook.PublishYear);
            Assert.Equal(720, storedAudiobook.Runtime);
            Assert.Equal("Collector Edition", storedAudiobook.Edition);
            Assert.Equal("Edited Version", storedAudiobook.Version);
            Assert.Equal("Edited Universe", storedAudiobook.Series);
            Assert.Equal("4", storedAudiobook.SeriesNumber);
            Assert.NotNull(storedAudiobook.SeriesMemberships);
            Assert.Collection(
                storedAudiobook.SeriesMemberships!,
                membership =>
                {
                    Assert.Equal("Edited Universe", membership.SeriesName);
                    Assert.Equal("4", membership.SeriesNumber);
                    Assert.True(membership.IsPrimary);
                },
                membership =>
                {
                    Assert.Equal("Anthology Line", membership.SeriesName);
                    Assert.Equal("12", membership.SeriesNumber);
                    Assert.False(membership.IsPrimary);
                });
            Assert.Equal(new List<string> { "Sci-Fi", "Adventure" }, storedAudiobook.Genres);
            Assert.Equal("https://example.com/edited.jpg", storedAudiobook.ImageUrl);
            Assert.Equal(new List<string> { "tag-two" }, storedAudiobook.Tags);
            Assert.False(storedAudiobook.Monitored);
            Assert.True(storedAudiobook.Explicit);
            Assert.True(storedAudiobook.Abridged);
        }

        [Fact]
        public async Task UpdateAudiobook_PreCanceledRequest_DoesNotMutateAudiobook()
        {
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Original",
                Monitored = true
            });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var update = _provider
                .GetRequiredService<LibraryController>()
                .UpdateAudiobook(
                    audiobook.Id,
                    new AudiobookUpdateRequest
                    {
                        Title = "Canceled update",
                        Monitored = false
                    },
                    cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.Equal("Original", stored.Title);
            Assert.True(stored.Monitored);
        }

        [Fact]
        public async Task UpdateAudiobook_RepositoryReportsMissing_ReturnsNotFound()
        {
            var audiobook = new Audiobook
            {
                Id = 7001,
                Title = "Original"
            };
            var repository = new Mock<IAudiobookRepository>(
                MockBehavior.Strict);
            repository.Setup(service => service.GetForUpdateSnapshotAsync(
                    audiobook.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.UpdateAsync(
                    It.Is<Audiobook>(candidate =>
                        candidate.Id == audiobook.Id
                        && candidate.Title == "Updated")))
                .ReturnsAsync(false);
            Init(services => services.WithSingleton(repository.Object));

            var result = await _provider
                .GetRequiredService<LibraryController>()
                .UpdateAudiobook(
                    audiobook.Id,
                    new AudiobookUpdateRequest { Title = "Updated" });

            Assert.IsType<NotFoundObjectResult>(result);
            repository.Verify(service => service.GetForUpdateSnapshotAsync(
                audiobook.Id,
                It.IsAny<CancellationToken>()), Times.Once);
            repository.Verify(service => service.GetByIdAsync(audiobook.Id),
                Times.Once);
            repository.Verify(service => service.UpdateAsync(
                It.Is<Audiobook>(candidate => candidate.Id == audiobook.Id)),
                Times.Once);
        }

        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "OmittedBooleansRemainUnchanged")]
        public async Task UpdateAudiobook_OmittedBooleansRemainUnchanged()
        {
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Original",
                Monitored = false,
                Explicit = true,
                Abridged = true
            });
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.UpdateAudiobook(audiobook.Id, new AudiobookUpdateRequest
            {
                Title = "Edited"
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal("Edited", updated.Title);
            Assert.False(updated.Monitored);
            Assert.True(updated.Explicit);
            Assert.True(updated.Abridged);
        }

        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "LegacyBasePathCompatibilityRewritesReferences")]
        public async Task UpdateAudiobook_LegacyBasePathChange_RewritesStoredAbsoluteReferences()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-update-basepath-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Update Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-update-basepath-source");
            var targetPath = Path.Join(rootPath, "Author", "Title");
            var unrelatedPath = Path.Join(FileService.GetTempPath(), "outside", "bonus.mp3");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Legacy Path Update",
                BasePath = sourcePath,
                FilePath = Path.Join(sourcePath, "book.m4b"),
                ImageUrl = Path.Join(sourcePath, "cover.jpg"),
                Files =
                [
                    new AudiobookFile { Path = Path.Join(sourcePath, "book.m4b") },
                    new AudiobookFile { Path = Path.Join("disc-1", "chapter.mp3") },
                    new AudiobookFile { Path = unrelatedPath }
                ]
            });
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.UpdateAudiobook(audiobook.Id, new AudiobookUpdateRequest
            {
                BasePath = targetPath,
                Title = "Legacy Path Update Edited"
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal("Legacy Path Update Edited", updated.Title);
            Assert.Equal(FileUtils.NormalizeStoredPath(targetPath), updated.BasePath);
            Assert.Equal(Path.Join(targetPath, "book.m4b"), updated.FilePath);
            Assert.Equal(Path.Join(targetPath, "cover.jpg"), updated.ImageUrl);
            Assert.Contains(updated.Files!, file => file.Path == Path.Join(targetPath, "book.m4b"));
            Assert.Contains(updated.Files!, file => file.Path == Path.Join("disc-1", "chapter.mp3"));
            Assert.Contains(updated.Files!, file => file.Path == unrelatedPath);
        }

        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "LegacyBasePathCompatibilityIgnoresStalePathFields")]
        public async Task UpdateAudiobook_LegacyBasePathChange_DoesNotRestoreStaleFileOrImagePaths()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-update-stale-path-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Stale Path Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-update-stale-path-source");
            var targetPath = Path.Join(rootPath, "Author", "Title");
            var staleFilePath = Path.Join(sourcePath, "book.m4b");
            var staleImagePath = Path.Join(sourcePath, "cover.jpg");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Stale Path Payload",
                BasePath = sourcePath,
                FilePath = staleFilePath,
                ImageUrl = staleImagePath,
                Files = [new AudiobookFile { Path = staleFilePath }]
            });
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.UpdateAudiobook(audiobook.Id, new AudiobookUpdateRequest
            {
                BasePath = targetPath,
                FilePath = staleFilePath,
                ImageUrl = staleImagePath,
                Title = "Stale Path Payload Edited"
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal("Stale Path Payload Edited", updated.Title);
            Assert.Equal(FileUtils.NormalizeStoredPath(targetPath), updated.BasePath);
            Assert.Equal(Path.Join(targetPath, "book.m4b"), updated.FilePath);
            Assert.Equal(Path.Join(targetPath, "cover.jpg"), updated.ImageUrl);
            Assert.Contains(updated.Files!, file => file.Path == Path.Join(targetPath, "book.m4b"));
            Assert.DoesNotContain(updated.Files!, file => file.Path == staleFilePath);
        }

        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "LegacyBasePathCompatibilityPreservesExplicitExternalImageUpdate")]
        public async Task UpdateAudiobook_LegacyBasePathChange_AppliesExplicitExternalImageUrl()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-update-image-url-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Image URL Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-update-image-url-source");
            var targetPath = Path.Join(rootPath, "Author", "Title");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "External Image Update",
                BasePath = sourcePath,
                ImageUrl = Path.Join(sourcePath, "cover.jpg")
            });
            const string replacementImageUrl = "https://cdn.example.test/replacement.jpg";
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.UpdateAudiobook(audiobook.Id, new AudiobookUpdateRequest
            {
                BasePath = targetPath,
                ImageUrl = replacementImageUrl
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(targetPath), updated.BasePath);
            Assert.Equal(replacementImageUrl, updated.ImageUrl);
        }

        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "MetadataOnlyUpdateStillAppliesExplicitImagePathField")]
        public async Task UpdateAudiobook_MetadataOnlyUpdate_StillAllowsExplicitImagePathAssignments()
        {
            var basePath = FileService.GetTempDirectory("listenarr-update-metadata-path-base");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Metadata Path Update",
                BasePath = basePath,
                ImageUrl = Path.Join(basePath, "original.jpg")
            });
            var updatedImagePath = Path.Join(basePath, "relinked.jpg");
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.UpdateAudiobook(audiobook.Id, new AudiobookUpdateRequest
            {
                Title = "Metadata Path Update Edited",
                BasePath = basePath,
                ImageUrl = updatedImagePath
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal("Metadata Path Update Edited", updated.Title);
            Assert.Equal(basePath, updated.BasePath);
            Assert.Equal(updatedImagePath, updated.ImageUrl);
        }

        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "LegacyBasePathCompatibilityRejectsInvalidDestination")]
        public async Task UpdateAudiobook_LegacyBasePathOutsideConfiguredRoots_IsRejected()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-update-valid-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Valid Update Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-update-original-source");
            var originalFilePath = Path.Join(sourcePath, "book.m4b");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Invalid Legacy Path Update",
                BasePath = sourcePath,
                FilePath = originalFilePath,
                Files = [new AudiobookFile { Path = originalFilePath }]
            });
            var outsidePath = Path.Join(FileService.GetTempDirectory("listenarr-update-outside-root"), "Author", "Title");
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.UpdateAudiobook(audiobook.Id, new AudiobookUpdateRequest
            {
                BasePath = outsidePath,
                Title = "Should Not Persist"
            });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("configured root folder or output path", badRequest.Value?.ToString() ?? string.Empty);

            var unchanged = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(unchanged);
            Assert.Equal("Invalid Legacy Path Update", unchanged.Title);
            Assert.Equal(sourcePath, unchanged.BasePath);
            Assert.Equal(originalFilePath, unchanged.FilePath);
            Assert.Equal(originalFilePath, Assert.Single(unchanged.Files!).Path);
        }

        private async Task<Audiobook?> GetFreshAudiobookAsync(int id)
        {
            using var scope = _provider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            return await repository.GetByIdAsync(id);
        }
    }
}
