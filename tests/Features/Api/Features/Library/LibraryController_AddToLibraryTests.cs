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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "LibraryController_AddToLibraryTests")]
    [Trait("Category", "LibraryController")]
    public sealed class LibraryController_AddToLibraryTests : BaseTests
    {
        private readonly Mock<IImageCacheService> imageCacheServiceMock = new Mock<IImageCacheService>();
        private readonly Mock<ILibraryDestinationMutationGuard> destinationGuardMock = new();
        private readonly string imageUrl1 = "http://example.com/a1.jpg";
        private readonly string imageUrl2 = "http://example.com/a2.jpg";
        private string tempRoot = null!;

        public override async Task InitializeAsync()
        {
            imageCacheServiceMock
                .Setup(m => m.MoveToLibraryStorageAsync("B000TEST01", null))
                .ReturnsAsync("config/cache/images/library/B000TEST01.jpg");
            imageCacheServiceMock
                .Setup(m => m.MoveToLibraryStorageAsync(
                    It.Is<string>(value => value.StartsWith("img-", StringComparison.Ordinal)),
                    null))
                .ReturnsAsync("config/cache/images/library/derived.jpg");
            destinationGuardMock
                .Setup(guard => guard.GetBlockingReasonAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            Init(services => services
                .WithSingleton(imageCacheServiceMock.Object)
                .WithSingleton(destinationGuardMock.Object)
                .WithScoped<ILibraryAddCommitStore>(provider =>
                    new InMemoryLibraryAddCommitStore(
                        provider.GetRequiredService<ListenArrDbContext>())));
            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            tempRoot = FileService.GetTempDirectory("listenarr-test");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}")
                .WithFileNamingPattern("{Title}")
                .Build());

            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithIsDefault()
                .WithPath(tempRoot)
                .Build());
        }

        private async Task ReinitializeAsync(Action<ServiceCollectionBuilder> configure)
        {
            Init(builder =>
            {
                builder
                    .WithSingleton(imageCacheServiceMock.Object)
                    .WithSingleton(destinationGuardMock.Object)
                    .WithScoped<ILibraryAddCommitStore>(provider =>
                        new InMemoryLibraryAddCommitStore(
                            provider.GetRequiredService<ListenArrDbContext>()));
                configure(builder);
            });
            await InitDataAsync();
        }

        [Fact]
        public async Task AddToLibrary_FinalCommitWaitsForGlobalFilesystemMutation()
        {
            var coordinator = _provider.GetRequiredService<IFilesystemMutationCoordinator>();
            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var addTask = _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Globally Coordinated",
                        Author = "Author"
                    },
                    Monitored = true
                });
            await Task.Delay(50);
            Assert.False(addTask.IsCompleted);

            releaseLock.SetResult();
            await blocker;
            Assert.IsType<OkObjectResult>(await addTask);
            Assert.Single(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddToLibrary_ConcurrentIdentifierlessSameDestination_CommitsExactlyOnce()
        {
            var destination = Path.Join(tempRoot, "Identifierless", "Book");
            LibraryController.AddToLibraryRequest CreateRequest() => new()
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Identifierless Duplicate Probe"
                },
                DestinationPath = destination,
                Monitored = true
            };
            var controller = _provider.GetRequiredService<LibraryController>();

            var results = await Task.WhenAll(
                controller.AddToLibrary(CreateRequest()),
                controller.AddToLibrary(CreateRequest()));

            Assert.Single(results, result => result is OkObjectResult);
            Assert.Single(results, result => result is ConflictObjectResult);
            var persisted = await _audiobookRepository.GetAllAsync();
            var audiobook = Assert.Single(persisted);
            Assert.Equal("Identifierless Duplicate Probe", audiobook.Title);
            Assert.Equal(Path.GetFullPath(destination), Path.GetFullPath(audiobook.BasePath!));
        }

        [Fact]
        public async Task AddToLibrary_IdentifierlessSameDestination_WithProductionGuardReturnsExistingConflict()
        {
            await ReinitializeAsync(builder => builder
                .WithScoped<ILibraryDestinationMutationGuard>(provider =>
                    new LibraryDestinationMutationGuard(
                        provider.GetRequiredService<IRootFolderService>(),
                        provider.GetRequiredService<IRootFolderRelocationService>(),
                        provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                        provider.GetRequiredService<IAudiobookRepository>())));
            var root = Assert.Single(await _rootFolderRepository.GetAllAsync());
            var rootResolution = await _provider
                .GetRequiredService<IFileSystemSemanticsResolver>()
                .ResolveAsync(root.Path, FileSystemCaseSensitivityMode.Auto);
            Assert.Equal(PathIdentityState.Valid, rootResolution.State);
            root.ResolvedCaseSensitivity = rootResolution.Semantics.CaseSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                root.Path,
                rootResolution.Semantics);
            await _rootFolderRepository.UpdateAsync(root);

            var destination = Path.Join(tempRoot, "Identifierless", "Production Guard");
            LibraryController.AddToLibraryRequest CreateRequest() => new()
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Identifierless Production Guard"
                },
                DestinationPath = destination,
                Monitored = true
            };
            var controller = _provider.GetRequiredService<LibraryController>();

            var first = await controller.AddToLibrary(CreateRequest());
            var second = await controller.AddToLibrary(CreateRequest());

            Assert.IsType<OkObjectResult>(first);
            var conflict = Assert.IsType<ConflictObjectResult>(second);
            var persisted = Assert.Single(await _audiobookRepository.GetAllAsync());
            var payload = System.Text.Json.JsonSerializer.Serialize(
                conflict.Value,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            Assert.Equal(
                "Audiobook already exists in library",
                document.RootElement.GetProperty("message").GetString());
            Assert.Equal(
                persisted.Id,
                document.RootElement.GetProperty("audiobook").GetProperty("id").GetInt32());
        }

        [Fact]
        public async Task AddToLibrary_DifferentBookSameDestination_ReturnsDestinationConflict()
        {
            await ReinitializeAsync(builder => builder
                .WithScoped<ILibraryDestinationMutationGuard>(provider =>
                    new LibraryDestinationMutationGuard(
                        provider.GetRequiredService<IRootFolderService>(),
                        provider.GetRequiredService<IRootFolderRelocationService>(),
                        provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                        provider.GetRequiredService<IAudiobookRepository>())));
            var root = Assert.Single(await _rootFolderRepository.GetAllAsync());
            var rootResolution = await _provider
                .GetRequiredService<IFileSystemSemanticsResolver>()
                .ResolveAsync(root.Path, FileSystemCaseSensitivityMode.Auto);
            Assert.Equal(PathIdentityState.Valid, rootResolution.State);
            root.ResolvedCaseSensitivity = rootResolution.Semantics.CaseSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                root.Path,
                rootResolution.Semantics);
            await _rootFolderRepository.UpdateAsync(root);

            var destination = Path.Join(tempRoot, "Shared Destination");
            var existingAudiobook = new AudiobookBuilder()
                .WithTitle("Existing Book")
                .WithBasePath(destination)
                .Build();
            existingAudiobook.Asin = "EXISTING-ASIN";
            var existing = await _audiobookRepository.AddAsync(existingAudiobook);

            var result = await _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Different Book",
                        Asin = "DIFFERENT-ASIN",
                        Author = "Different Author"
                    },
                    DestinationPath = destination,
                    Monitored = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = System.Text.Json.JsonSerializer.Serialize(
                badRequest.Value,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            Assert.Equal(
                "destination_path_blocked",
                document.RootElement.GetProperty("code").GetString());
            Assert.Contains(
                "already assigned",
                document.RootElement.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
            var persisted = await _audiobookRepository.GetAllAsync();
            Assert.Single(persisted);
            Assert.Equal(existing.Id, persisted[0].Id);
        }

        [LinuxFact]
        public async Task AddToLibrary_StalePersistedInsensitiveRoot_DoesNotTurnCaseDistinctDestinationIntoExistingConflict()
        {
            await ReinitializeAsync(builder => builder
                .WithScoped<ILibraryDestinationMutationGuard>(provider =>
                    new LibraryDestinationMutationGuard(
                        provider.GetRequiredService<IRootFolderService>(),
                        provider.GetRequiredService<IRootFolderRelocationService>(),
                        provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                        provider.GetRequiredService<IAudiobookRepository>())));
            var persistedSemantics = new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Insensitive);
            var root = Assert.Single(await _rootFolderRepository.GetAllAsync());
            root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            root.ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                root.Path,
                persistedSemantics);
            await _rootFolderRepository.UpdateAsync(root);

            var existingPath = Path.Join(tempRoot, "CaseBook");
            var requestedPath = Path.Join(tempRoot, "casebook");
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Existing Case Book")
                .WithBasePath(existingPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Distinct Case Book"
                    },
                    DestinationPath = requestedPath,
                    Monitored = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(
                "filesystem identity is unavailable",
                badRequest.Value?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddToLibrary_IdentifierlessDifferentDestination_RemainsAllowed()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            LibraryController.AddToLibraryRequest CreateRequest(string destination) => new()
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Identifierless Distinct Destination"
                },
                DestinationPath = destination,
                Monitored = true
            };

            Assert.IsType<OkObjectResult>(await controller.AddToLibrary(
                CreateRequest(Path.Join(tempRoot, "Edition A"))));
            Assert.IsType<OkObjectResult>(await controller.AddToLibrary(
                CreateRequest(Path.Join(tempRoot, "Edition B"))));

            Assert.Equal(2, (await _audiobookRepository.GetAllAsync()).Count);
        }

        [Fact]
        public async Task AddToLibrary_BlockedAuthorLookupDoesNotBlockUnrelatedFilesystemMutation()
        {
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(
                httpClient,
                NullLogger<AudibleService>.Instance);
            var lookupEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLookup = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            audible
                .Setup(service => service.LookupAuthorAsync("Slow Author", "us"))
                .Returns(async () =>
                {
                    lookupEntered.SetResult();
                    await releaseLookup.Task;
                    return null;
                });
            await ReinitializeAsync(builder =>
                builder.WithSingleton<AudibleService>(audible.Object));

            var addTask = _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Slow Enrichment",
                        Authors = ["Slow Author"]
                    },
                    Monitored = true
                });
            await lookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var unrelatedMutationCompleted = false;
            await _provider
                .GetRequiredService<IFilesystemMutationCoordinator>()
                .ExecuteExclusiveAsync(_ =>
                {
                    unrelatedMutationCompleted = true;
                    return Task.CompletedTask;
                })
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(unrelatedMutationCompleted);
            Assert.False(addTask.IsCompleted);
            releaseLookup.SetResult();
            Assert.IsType<OkObjectResult>(await addTask);
        }

        [Fact]
        public async Task AddToLibrary_NullAuthors_UsesUniformEnrichmentPipelineWithoutLookup()
        {
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(
                MockBehavior.Strict,
                httpClient,
                NullLogger<AudibleService>.Instance);
            await ReinitializeAsync(builder =>
                builder.WithSingleton<AudibleService>(audible.Object));

            var result = await _provider
                .GetRequiredService<LibraryController>()
                .AddToLibrary(new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "No Author Enrichment",
                        Authors = null
                    },
                    Monitored = true
                });

            Assert.IsType<OkObjectResult>(result);
            var audiobook = Assert.Single(
                await _audiobookRepository.GetAllAsync());
            Assert.NotNull(audiobook.AuthorAsins);
            Assert.Empty(audiobook.AuthorAsins);
            audible.Verify(
                service => service.LookupAuthorAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task AddToLibrary_AuthorEnrichment_NormalizesDeduplicatesAndBoundsLookups()
        {
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(
                httpClient,
                NullLogger<AudibleService>.Instance);
            audible.Setup(service => service.LookupAuthorAsync(
                    It.IsAny<string>(),
                    "us"))
                .ReturnsAsync((AuthorLookupItem?)null);
            await ReinitializeAsync(builder =>
                builder.WithSingleton<AudibleService>(audible.Object));
            var authors = new List<string>
            {
                " ",
                " Alice Author ",
                "alice author"
            };
            authors.AddRange(Enumerable.Range(0, 40)
                .Select(index => $"Author {index:00}"));

            var result = await _provider
                .GetRequiredService<LibraryController>()
                .AddToLibrary(new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Bounded Author Enrichment",
                        Authors = authors
                    },
                    Monitored = true
                });

            Assert.IsType<OkObjectResult>(result);
            audible.Verify(service => service.LookupAuthorAsync(
                "Alice Author",
                "us"), Times.Once);
            audible.Verify(service => service.LookupAuthorAsync(
                "alice author",
                "us"), Times.Never);
            audible.Verify(service => service.LookupAuthorAsync(
                It.IsAny<string>(),
                "us"), Times.Exactly(32));
            audible.Verify(service => service.LookupAuthorAsync(
                "Author 31",
                "us"), Times.Never);
        }

        [Fact]
        public async Task AddToLibrary_CancelledWhileWaitingForCommitGate_DoesNotPersist()
        {
            var coordinator = _provider.GetRequiredService<IFilesystemMutationCoordinator>();
            var lockEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;
            using var cancellation = new CancellationTokenSource();

            try
            {
                var addTask = _provider.GetRequiredService<LibraryController>().AddToLibrary(
                    new LibraryController.AddToLibraryRequest
                    {
                        Metadata = new AudibleBookMetadata
                        {
                            Title = "Cancelled Commit",
                            Authors = []
                        },
                        Monitored = true
                    },
                    cancellation.Token);
                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => addTask);
                Assert.Empty(await _audiobookRepository.GetAllAsync());
            }
            finally
            {
                releaseLock.TrySetResult();
                await blocker;
            }
        }

        [Fact]
        public async Task AddToLibrary_PostCommitWebhookFailure_DoesNotFailCommittedAdd()
        {
            var notifications = new Mock<INotificationService>();
            notifications
                .Setup(service => service.SendNotificationAsync(
                    It.IsAny<string>(),
                    It.IsAny<object>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>()))
                .ThrowsAsync(new HttpRequestException("webhook unavailable"));
            await ReinitializeAsync(builder =>
                builder.WithSingleton<INotificationService>(notifications.Object));

            var result = await _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Committed Despite Side Effects",
                        Authors = []
                    },
                    Monitored = true
                });

            Assert.IsType<OkObjectResult>(result);
            var audiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            var history = Assert.Single(await _historyRepository.GetByAudiobookIdAsync(audiobook.Id));
            Assert.Equal("Added", history.EventType);
            Assert.Equal(audiobook.Id, history.AudiobookId);
            notifications.Verify(
                service => service.SendNotificationAsync(
                    "book-added",
                    It.IsAny<object>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>()),
                Times.Once);
        }

        [Fact]
        public async Task AddToLibrary_AtomicCommitFailure_PersistsNeitherAudiobookNorHistory()
        {
            imageCacheServiceMock
                .Setup(service => service.DownloadAndCacheImageAsync(
                    imageUrl1,
                    "B000TEST01"))
                .ReturnsAsync("config/cache/images/temp/B000TEST01.jpg");
            var commitStore = new Mock<ILibraryAddCommitStore>();
            commitStore
                .Setup(store => store.CommitAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<History>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("commit unavailable"));
            await ReinitializeAsync(builder =>
                builder.WithSingleton<ILibraryAddCommitStore>(commitStore.Object));

            var controller = _provider.GetRequiredService<LibraryController>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.AddToLibrary(new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Atomic Commit Failure",
                        Asin = "B000TEST01",
                        ImageUrl = imageUrl1,
                        Authors = []
                    },
                    Monitored = true
                }));

            Assert.Empty(await _audiobookRepository.GetAllAsync());
            Assert.Equal(0, await _historyRepository.CountAsync());
            imageCacheServiceMock.Verify(
                service => service.DownloadAndCacheImageAsync(
                    imageUrl1,
                    "B000TEST01"),
                Times.Once);
            imageCacheServiceMock.Verify(
                service => service.MoveToLibraryStorageAsync(
                    "B000TEST01",
                    It.IsAny<string?>()),
                Times.Never);
        }

        [Fact]
        public async Task AddToLibrary_UsesLegacyAuthorField_PopulatesAuthorsAndBasePath()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Legacy Title",
                    Author = "Legacy Author"
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.NotNull(stored.Authors);
            Assert.Contains("Legacy Author", stored.Authors);
            Assert.Equal(Path.Join(tempRoot, "Legacy Author"), stored.BasePath);
        }

        [Fact]
        public async Task AddToLibrary_RejectsDestinationProtectedByActiveRelocation()
        {
            destinationGuardMock
                .Setup(guard => guard.GetBlockingReasonAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("Destination overlaps an active root folder relocation.");
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.AddToLibrary(new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Relocation Conflict",
                    Author = "Blocked Author",
                    ImageUrl = imageUrl1
                },
                DestinationPath = Path.Join(tempRoot, "Blocked Author", "Relocation Conflict")
            });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("active root folder relocation", badRequest.Value?.ToString() ?? string.Empty);
            Assert.DoesNotContain(
                await _audiobookRepository.GetAllAsync(),
                audiobook => audiobook.Title == "Relocation Conflict");
            imageCacheServiceMock.Verify(
                service => service.MoveToLibraryStorageAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>()),
                Times.Never);
        }

        [Fact]
        public async Task AddToLibrary_WithGeneratedPathFromSanitizedMetadata_Succeeds()
        {
            var settings = await _applicationSettingsRepository.GetAsync()
                ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                    new ApplicationSettingsBuilder().Build());
            settings.FolderNamingPattern = "{Author}/{Title}";
            settings.FileNamingPattern = "{Title}";
            await _applicationSettingsRepository.SaveAsync(settings);
            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Book: The Ending.",
                    Author = "CON"
                },
                Monitored = true
            };

            var actionResult = await controller.AddToLibrary(request);

            Assert.IsType<OkObjectResult>(actionResult);
            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.Equal(Path.Join(tempRoot, "CON_", "Book - The Ending"), stored.BasePath);
        }

        [Fact]
        public async Task AddToLibrary_PersistsEditableMetadataFields()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Editable Title",
                    Subtitle = "Editable Subtitle",
                    Authors = new List<string> { "Edited Author" },
                    Narrators = new List<string> { "Edited Narrator" },
                    Publisher = "Edited Publisher",
                    Language = "english",
                    Runtime = 615,
                    Edition = "Collector's Edition",
                    Version = "Audible Version",
                    Asin = "B00EDIT123",
                    Isbn = new List<string> { "9781234567890" },
                    OpenLibraryId = "OL12345M"
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.Equal("Editable Title", stored.Title);
            Assert.Equal("Editable Subtitle", stored.Subtitle);
            Assert.Equal("Edited Publisher", stored.Publisher);
            Assert.Equal("english", stored.Language);
            Assert.Equal(615, stored.Runtime);
            Assert.Equal("Collector's Edition", stored.Edition);
            Assert.Equal("Audible Version", stored.Version);
            Assert.Equal("B00EDIT123", stored.Asin);
            Assert.Equal("OL12345M", stored.OpenLibraryId);
            Assert.NotNull(stored.Authors);
            Assert.Contains("Edited Author", stored.Authors);
            Assert.NotNull(stored.Narrators);
            Assert.Contains("Edited Narrator", stored.Narrators);
            Assert.NotNull(stored.Isbn);
            Assert.Contains("9781234567890", stored.Isbn);
        }

        [Fact]
        public async Task AddToLibrary_WithAsinRegion_PersistsIdentifierRegion()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Region Title",
                    Authors = new List<string> { "Region Author" },
                    Asin = "B00REGION1",
                    Region = "de"
                },
                Monitored = true
            };

            var actionResult = await controller.AddToLibrary(request);

            Assert.IsType<OkObjectResult>(actionResult);

            var stored = await _audiobookRepository.GetByAsinAsync("B00REGION1");
            Assert.NotNull(stored);
            var asinIdentifier = Assert.Single(
                stored.ExternalIdentifiers ?? new List<AudiobookExternalIdentifier>(),
                identifier => identifier.Type == AudiobookExternalIdentifierType.Asin);
            Assert.Equal("de", asinIdentifier.Region);
            Assert.True(asinIdentifier.IsPrimary);
        }

        [Fact]
        public async Task AddToLibrary_WithAsin_MovesImageToLibraryStorage()
        {
            var asin = "B000TEST01";

            // Configuration service providing an OutputPath root
            var tempRoot = FileService.GetTempDirectory("listenarr-test");

            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Move Test",
                    Author = "A Uthor",
                    Asin = "B000TEST01",
                    ImageUrl = imageUrl1
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.Equal($"/config/cache/images/library/B000TEST01.jpg", stored.ImageUrl);
            imageCacheServiceMock.Verify(m => m.DownloadAndCacheImageAsync(imageUrl1, asin), Times.Once);
            imageCacheServiceMock.Verify(m => m.MoveToLibraryStorageAsync(asin, null), Times.Once);
        }

        [Fact]
        public async Task AddToLibrary_WithoutAsin_UsesDerivedKey_AndMovesImageToLibraryStorage()
        {
            // Configuration service providing an OutputPath root
            var tempRoot = FileService.GetTempDirectory("listenarr-test");

            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Derived Test",
                    Author = "Some Author",
                    ImageUrl = imageUrl2
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.Equal($"/config/cache/images/library/derived.jpg", stored.ImageUrl);
            imageCacheServiceMock.Verify(
                m => m.DownloadAndCacheImageAsync(
                    imageUrl2,
                    It.Is<string>(value => value.StartsWith("img-", StringComparison.Ordinal))),
                Times.Once);
            imageCacheServiceMock.Verify(
                m => m.MoveToLibraryStorageAsync(
                    It.Is<string>(value => value.StartsWith("img-", StringComparison.Ordinal)),
                    null),
                Times.Once);
        }

        [Fact]
        public async Task AddToLibrary_ImagePreparationFailure_UsesFallbackAndCommitsHistory()
        {
            const string asin = "B000FALLBACK";
            const string imageUrl = "https://example.com/fallback.jpg";
            imageCacheServiceMock
                .Setup(service => service.DownloadAndCacheImageAsync(imageUrl, asin))
                .ThrowsAsync(new HttpRequestException("image unavailable"));
            imageCacheServiceMock
                .Setup(service => service.MoveToLibraryStorageAsync(asin, null))
                .ReturnsAsync((string?)null);

            var result = await _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Fallback Image",
                        Asin = asin,
                        ImageUrl = imageUrl,
                        Authors = []
                    },
                    Monitored = true
                });

            Assert.IsType<OkObjectResult>(result);
            var audiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.Equal(imageUrl, audiobook.ImageUrl);
            Assert.Single(await _historyRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        public async Task AddToLibrary_InvalidDestination_DoesNotStartExternalPreparation()
        {
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(
                httpClient,
                NullLogger<AudibleService>.Instance);
            await ReinitializeAsync(builder =>
                builder.WithSingleton<AudibleService>(audible.Object));
            imageCacheServiceMock.Invocations.Clear();
            var outsideRoot = FileService.GetTempDirectory("outside-add-root");

            var result = await _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Invalid Destination",
                        Asin = "B000INVALID",
                        ImageUrl = imageUrl1,
                        Authors = ["External Author"]
                    },
                    DestinationPath = Path.Join(outsideRoot, "Book"),
                    Monitored = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
            imageCacheServiceMock.Verify(
                service => service.DownloadAndCacheImageAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
            audible.Verify(
                service => service.LookupAuthorAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task AddToLibrary_ConfiguredRootsExist_LegacyOutputPathDoesNotGrantIndependentDestinationAuthority()
        {
            var legacyOutputPath = FileService.GetTempDirectory(
                "listenarr-stale-output");
            var settings = await _applicationSettingsRepository.GetAsync()
                ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                    new ApplicationSettingsBuilder().Build());
            settings.OutputPath = legacyOutputPath;
            await _applicationSettingsRepository.SaveAsync(settings);
            var destination = Path.Join(legacyOutputPath, "Legacy Author", "Legacy Book");

            var result = await _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Legacy Output Authority",
                        Author = "Legacy Author"
                    },
                    DestinationPath = destination,
                    Monitored = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
            Assert.Empty(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddToLibrary_WithCustomPath_StoresCustomPathAsBasePath()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var customPath = Path.Join(tempRoot, "custom", "audiobooks", "Author", "Series", "Title");
            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Custom Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath  // Custom path provided
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            var expectedPath = Path.GetFullPath(customPath);
            Assert.Equal(expectedPath, stored.BasePath);
        }

        [Fact]
        public async Task AddToLibrary_InvalidLegacyRootDoesNotBlockValidConfiguredDestination()
        {
            var invalidLegacyRoot = "invalid\0legacy-root";
            await _rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "Invalid legacy root",
                Path = invalidLegacyRoot
            });
            var destination = Path.Join(tempRoot, "Valid Author", "Valid Title");

            var result = await _provider.GetRequiredService<LibraryController>().AddToLibrary(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Valid Destination With Legacy Root",
                        Author = "Valid Author"
                    },
                    Monitored = true,
                    DestinationPath = destination
                });

            Assert.IsType<OkObjectResult>(result);
            var audiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.EndsWith(
                Path.Join("Valid Author", "Valid Title"),
                audiobook.BasePath,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExplicitManagedDestination_DoesNotRequireLegacySettingsRead_InServiceOrFallbackWorkflow()
        {
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService.Setup(service => service.GetApplicationSettingsAsync())
                .ThrowsAsync(new InvalidOperationException("Injected legacy settings outage."));
            await ReinitializeAsync(builder => builder
                .WithSingleton<IConfigurationService>(configurationService.Object));

            var serviceDestination = Path.Join(
                tempRoot,
                "Settings Independent",
                "Service Book");
            var serviceResult = await _provider
                .GetRequiredService<LibraryController>()
                .AddToLibrary(new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Settings Independent Service Book",
                        Author = "Settings Independent"
                    },
                    DestinationPath = serviceDestination,
                    Monitored = true
                });
            Assert.IsType<OkObjectResult>(serviceResult);

            var workflow = new LibraryAddWorkflow(
                _provider.GetRequiredService<IAudiobookRepository>(),
                _provider.GetRequiredService<IImageCacheService>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IHistoryRepository>(),
                _provider.GetRequiredService<ILibraryDestinationMutationGuard>(),
                _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
                _provider.GetRequiredService<ILogger<LibraryAddWorkflow>>());
            var fallbackDestination = Path.Join(
                tempRoot,
                "Settings Independent",
                "Fallback Book");
            var fallbackResult = await workflow.AddAsync(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Settings Independent Fallback Book",
                        Author = "Settings Independent"
                    },
                    DestinationPath = fallbackDestination,
                    Monitored = true
                });
            Assert.IsType<OkObjectResult>(fallbackResult);
            Assert.Equal(2, (await _audiobookRepository.GetAllAsync()).Count);
        }

        [Fact]
        public async Task AddWorkflowFallback_PostCommitNotificationSettingsFailure_ReturnsSuccessAndWritesHistory()
        {
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService.Setup(service => service.GetApplicationSettingsAsync())
                .ThrowsAsync(new InvalidOperationException("Injected notification settings outage."));
            await ReinitializeAsync(builder => builder
                .WithSingleton<IConfigurationService>(configurationService.Object));
            var notificationService = new Mock<INotificationService>(MockBehavior.Strict);
            var workflow = new LibraryAddWorkflow(
                _provider.GetRequiredService<IAudiobookRepository>(),
                _provider.GetRequiredService<IImageCacheService>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IHistoryRepository>(),
                _provider.GetRequiredService<ILibraryDestinationMutationGuard>(),
                _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
                _provider.GetRequiredService<ILogger<LibraryAddWorkflow>>(),
                notificationService.Object,
                libraryAddService: null);
            var destination = Path.Join(
                tempRoot,
                "Post Commit",
                "Notification Failure");

            var result = await workflow.AddAsync(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Post Commit Notification Failure",
                        Author = "Post Commit"
                    },
                    DestinationPath = destination,
                    Monitored = true
                });

            Assert.IsType<OkObjectResult>(result);
            var audiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.Single(await _historyRepository.GetByAudiobookIdAsync(audiobook.Id));
            notificationService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task AddWorkflowFallback_PostCommitHistoryFailure_ReturnsSuccess()
        {
            var historyRepository = new Mock<IHistoryRepository>(MockBehavior.Strict);
            historyRepository.Setup(repository => repository.AddAsync(It.IsAny<History>()))
                .ThrowsAsync(new IOException("Injected history outage."));
            var workflow = new LibraryAddWorkflow(
                _provider.GetRequiredService<IAudiobookRepository>(),
                _provider.GetRequiredService<IImageCacheService>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                historyRepository.Object,
                _provider.GetRequiredService<ILibraryDestinationMutationGuard>(),
                _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
                _provider.GetRequiredService<ILogger<LibraryAddWorkflow>>(),
                notificationService: null,
                libraryAddService: null);
            var destination = Path.Join(
                tempRoot,
                "Post Commit",
                "History Failure");

            var result = await workflow.AddAsync(
                new LibraryController.AddToLibraryRequest
                {
                    Metadata = new AudibleBookMetadata
                    {
                        Title = "Post Commit History Failure",
                        Author = "Post Commit"
                    },
                    DestinationPath = destination,
                    Monitored = true
                });

            Assert.IsType<OkObjectResult>(result);
            Assert.Single(await _audiobookRepository.GetAllAsync());
            historyRepository.VerifyAll();
        }

        [Fact]
        public async Task AddWorkflowFallback_InvalidLegacyRootDoesNotBlockValidConfiguredDestination()
        {
            var invalidLegacyRoot = "invalid\0fallback-root";
            await _rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "Invalid fallback root",
                Path = invalidLegacyRoot
            });
            var destination = Path.Join(tempRoot, "Fallback Author", "Fallback Title");
            var workflow = new LibraryAddWorkflow(
                _provider.GetRequiredService<IAudiobookRepository>(),
                _provider.GetRequiredService<IImageCacheService>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IHistoryRepository>(),
                _provider.GetRequiredService<ILibraryDestinationMutationGuard>(),
                _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
                _provider.GetRequiredService<ILogger<LibraryAddWorkflow>>());

            var result = await workflow.AddAsync(new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Fallback Valid Destination",
                    Author = "Fallback Author"
                },
                Monitored = true,
                DestinationPath = destination
            });

            Assert.IsType<OkObjectResult>(result);
            var audiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.EndsWith(
                Path.Join("Fallback Author", "Fallback Title"),
                audiobook.BasePath,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AddToLibrary_RejectsCustomPathOutsideConfiguredBoundariesWithStructuredError()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var outsideRoot = FileService.GetTempDirectory("listenarr-outside-add-root");
            var customPath = Path.Join(outsideRoot, "Author", "Title");
            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Outside Custom Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath
            };

            var actionResult = await controller.AddToLibrary(request);

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var payload = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            using var payloadDocument = System.Text.Json.JsonDocument.Parse(payload);
            var payloadRoot = payloadDocument.RootElement;
            Assert.Equal(
                "destination_path_outside_roots",
                payloadRoot.GetProperty("code").GetString());
            Assert.Equal("destinationPath", payloadRoot.GetProperty("field").GetString());
            Assert.EndsWith(
                Path.Join("Author", "Title"),
                payloadRoot.GetProperty("resolvedDestination").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddToLibrary_RejectsCustomPathWithLeadingWhitespaceBeforeAbsolutePath()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var customPath = " " + Path.Join(tempRoot, "custom", "audiobooks", "Author", "Title");
            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Leading Space Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath
            };

            var actionResult = await controller.AddToLibrary(request);

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.Contains("leading whitespace", badRequest.Value.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddWorkflowFallback_ReturnsStructuredDestinationValidationError()
        {
            var workflow = new LibraryAddWorkflow(
                _provider.GetRequiredService<IAudiobookRepository>(),
                _provider.GetRequiredService<IImageCacheService>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IHistoryRepository>(),
                _provider.GetRequiredService<ILibraryDestinationMutationGuard>(),
                _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
                _provider.GetRequiredService<ILogger<LibraryAddWorkflow>>());
            var customPath = " " + Path.Join(tempRoot, "custom", "audiobooks", "Author", "Title");

            var actionResult = await workflow.AddAsync(new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Fallback Leading Space Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath
            });

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var payload = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            Assert.Equal("destination_path_invalid", root.GetProperty("code").GetString());
            Assert.Equal("destinationPath", root.GetProperty("field").GetString());
            Assert.Equal(customPath, root.GetProperty("resolvedDestination").GetString());
        }

        [Fact]
        public async Task LibraryAddService_RejectsDestinationPathWithLeadingWhitespaceBeforeAbsolutePath()
        {
            var service = _provider.GetRequiredService<ILibraryAddService>();
            var customPath = " " + Path.Join(tempRoot, "custom", "audiobooks", "Author", "Title");
            var request = new LibraryAddOperationRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Leading Space Service Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath
            };

            var result = await service.AddToLibraryAsync(request);

            Assert.True(result.ValidationFailed);
            Assert.Contains("leading whitespace", result.ValidationMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddToLibrary_RejectsCustomPathParentTraversal()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var parentSegment = new string('.', 2);
            var customPath = Path.Join(tempRoot, "Books", parentSegment, "Other");
            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Traversal Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath
            };

            var actionResult = await controller.AddToLibrary(request);

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.Contains("DestinationPath", badRequest.Value.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddToLibrary_HandlesWrongCustomPath()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var customPath = "/custom/* ?|<>\0/Author/Series/Title";

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Custom Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath  // Custom path provided
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.Contains("DestinationPath", badRequest.Value.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await _audiobookRepository.GetAllAsync());
        }

        private sealed class InMemoryLibraryAddCommitStore(ListenArrDbContext db)
            : ILibraryAddCommitStore
        {
            public async Task CommitAsync(
                Audiobook audiobook,
                History history,
                CancellationToken cancellationToken = default)
            {
                db.Audiobooks.Add(audiobook);
                Assert.True(audiobook.Id > 0);
                history.AudiobookId = audiobook.Id;
                db.History.Add(history);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
