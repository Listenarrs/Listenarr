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
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using AppRootFolderService = Listenarr.Application.Audiobooks.RootFolders.RootFolderService;
using RootFolderService = Listenarr.Tests.Features.Application.Audiobooks.RootFolders.RootFolderServiceTestAdapter;

namespace Listenarr.Tests.Features.Application.Audiobooks.RootFolders
{
    public class RootFolderServiceTests
    {
        private string booksPath = FileUtils.GetAbsolutePath("books");
        private string rootPath = FileUtils.GetAbsolutePath("root");
        private string newRootPath = FileUtils.GetAbsolutePath("newroot");
        private string rootAuthorTitlePath = FileUtils.GetAbsolutePath("root", "Author", "Title");
        private string newRootAuthorTitlePath = FileUtils.GetAbsolutePath("newroot", "Author", "Title");

        private readonly ITestOutputHelper _output;
        public RootFolderServiceTests(ITestOutputHelper output) { _output = output; }

        [Fact]
        public void Constructor_Throws_WhenSemanticsResolverMissing()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var repo = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());

            Assert.Throws<ArgumentNullException>(() =>
                new AppRootFolderService(
                    repo,
                    null!,
                    semanticsResolver: null!,
                    Mock.Of<IMoveQueueService>(),
                    Mock.Of<IRootFolderRelocationService>(),
                    new FilesystemMutationCoordinator(),
                    new AudiobookOperationCoordinator()));
        }

        [Fact]
        public async Task Create_TargetReplacedAfterIdentityCapture_PersistsOriginalAuthorityAndMarksStorageUnsafe()
        {
            var parent = Path.Join(
                Path.GetTempPath(),
                "listenarr-tests",
                $"root-create-replaced-{Guid.NewGuid():N}");
            var rootPath = Path.Join(parent, "library");
            Directory.CreateDirectory(rootPath);
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var repository = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(
                repository,
                null,
                semanticsResolver: new FileSystemSemanticsResolver(),
                directoryObjectIdentityResolver: new DirectoryObjectIdentityResolver());
            service.AfterInitialDirectoryIdentityCapturedForTest = () =>
            {
                Directory.Move(rootPath, Path.Join(parent, "original"));
                Directory.CreateDirectory(rootPath);
            };

            var created = await service.CreateAsync(new RootFolder
            {
                Name = "Library",
                Path = rootPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            });

            Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, created.DirectoryObjectIdentityVersion);
            Assert.False(string.IsNullOrWhiteSpace(created.DirectoryObjectIdentity));
            Assert.False(string.IsNullOrWhiteSpace(created.DirectoryObjectIdentityUnavailableReason));
            var health = await new RootFolderStorageHealthResolver(
                new DirectoryObjectIdentityResolver()).ResolveAsync(created);
            Assert.Equal(RootFolderStorageState.Changed, health.State);
            Assert.False(health.CanMutateFilesystem);
        }

        [Fact]
        public async Task Create_MissingTarget_DoesNotManufactureAuthorization()
        {
            var parent = Path.Join(
                Path.GetTempPath(),
                "listenarr-tests",
                $"root-create-missing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(parent);
            var missingPath = Path.Join(parent, "library");
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var repository = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(
                repository,
                null,
                semanticsResolver: new FileSystemSemanticsResolver(),
                directoryObjectIdentityResolver: new DirectoryObjectIdentityResolver());

            var created = await service.CreateAsync(new RootFolder
            {
                Name = "Future Library",
                Path = missingPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            });

            Assert.Null(created.DirectoryObjectIdentityVersion);
            Assert.Null(created.DirectoryObjectIdentity);
            Assert.False(string.IsNullOrWhiteSpace(created.DirectoryObjectIdentityUnavailableReason));
            var healthResolver = new RootFolderStorageHealthResolver(
                new DirectoryObjectIdentityResolver());
            var missing = await healthResolver.ResolveAsync(created);
            Assert.Equal(RootFolderStorageState.Missing, missing.State);
            Assert.False(missing.CanConfirmCurrentFolder);

            Directory.CreateDirectory(missingPath);
            var appeared = await healthResolver.ResolveAsync(created);
            Assert.Equal(RootFolderStorageState.Unconfirmed, appeared.State);
            Assert.True(appeared.CanConfirmCurrentFolder);
            Assert.False(string.IsNullOrWhiteSpace(appeared.ConfirmationToken));
        }

        [Fact]
        public async Task Create_Throws_WhenPathDuplicate()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            db.RootFolders.Add(new RootFolder { Name = "A", Path = booksPath });
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(new RootFolder { Name = "B", Path = booksPath }));
        }

        [Theory]
        [InlineData("create")]
        [InlineData("update")]
        [InlineData("delete")]
        public async Task RootMutation_WaitsForSharedFilesystemMutationCoordinator(string operation)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var factory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(factory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var coordinator = new FilesystemMutationCoordinator();
            var service = new RootFolderService(
                repo,
                null,
                mutationCoordinator: coordinator);
            var root = new RootFolder { Name = "Existing", Path = rootPath };
            if (operation != "create")
            {
                await repo.AddAsync(root);
            }

            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var mutationTask = operation switch
            {
                "create" => service.CreateAsync(new RootFolder { Name = "Created", Path = newRootPath }),
                "update" => service.UpdateAsync(new RootFolder
                {
                    Id = root.Id,
                    Name = "Updated",
                    Path = root.Path
                }),
                "delete" => DeleteAndReturnAsync(service, root.Id),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            await Task.Delay(50);
            Assert.False(mutationTask.IsCompleted);

            releaseLock.SetResult();
            await Task.WhenAll(lockTask, mutationTask);
        }

        private static async Task<RootFolder> DeleteAndReturnAsync(
            RootFolderService service,
            int rootFolderId)
        {
            await service.DeleteAsync(rootFolderId);
            return new RootFolder();
        }

        [Fact]
        public async Task Create_PersistenceFailure_DoesNotWriteFilesystemIdentityMarker()
        {
            var directory = CreateTempDirectory("root-create-enrollment-compensation");
            var repository = new Mock<IRootFolderRepository>();
            repository.Setup(repo => repo.GetAllAsync()).ReturnsAsync([]);
            repository.Setup(repo => repo.AddAsync(It.IsAny<RootFolder>()))
                .ThrowsAsync(new InvalidOperationException("Injected persistence failure"));
            repository.Setup(repo => repo.GetByPathAsync(It.IsAny<string>()))
                .ReturnsAsync((RootFolder?)null);
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var service = new RootFolderService(
                repository.Object,
                null,
                relocationService: relocationService.Object,
                directoryObjectIdentityResolver: new DirectoryObjectIdentityResolver());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync(new RootFolder
                {
                    Name = "Library",
                    Path = directory
                }));
        }

        [Fact]
        public async Task Update_CaseSensitivityChange_RequiresIdentityMigrationWorkflow()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var repository = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var root = new RootFolder
            {
                Name = "Library",
                Path = rootPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            await repository.AddAsync(root);
            var service = new RootFolderService(repository, null);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(new RootFolder
                {
                    Id = root.Id,
                    Name = root.Name,
                    Path = root.Path,
                    IsDefault = root.IsDefault,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                }));

            Assert.Contains("path-change", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_MetadataOnly_WhenAuthorizedStorageIsOffline_DoesNotRequireFilesystemAccess()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var repository = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var root = new RootFolder
            {
                Name = "Library",
                Path = rootPath,
                CaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey("root", rootPath, semantics),
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = "listenarr-directory-v1:00000000000000000000000000000000:"
                    + new string('0', 64),
                DirectoryObjectIdentityUnavailableReason = "The storage location is currently missing."
            };
            await repository.AddAsync(root);
            var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
            var service = new RootFolderService(
                repository,
                null,
                directoryObjectIdentityResolver: identityResolver.Object);

            var updated = await service.UpdateAsync(new RootFolder
            {
                Id = root.Id,
                Name = "Renamed Library",
                Path = root.Path,
                IsDefault = root.IsDefault,
                CaseSensitivityMode = root.CaseSensitivityMode
            });

            Assert.Equal("Renamed Library", updated.Name);
            Assert.Equal(root.DirectoryObjectIdentity, updated.DirectoryObjectIdentity);
            Assert.Equal(
                "The storage location is currently missing.",
                updated.DirectoryObjectIdentityUnavailableReason);
            identityResolver.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Update_MetadataOnlyPreservesPersistedAutoSemantics()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var repository = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var semantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive);
            var root = new RootFolder
            {
                Name = "Library",
                Path = rootPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey("root", rootPath, semantics)
            };
            await repository.AddAsync(root);
            var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
            var service = new RootFolderService(
                repository,
                null,
                semanticsResolver: resolver.Object);

            var updated = await service.UpdateAsync(new RootFolder
            {
                Id = root.Id,
                Name = "Renamed",
                Path = root.Path,
                IsDefault = root.IsDefault,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            });

            Assert.Equal("Renamed", updated.Name);
            Assert.Equal(FileSystemCaseSensitivity.Sensitive, updated.ResolvedCaseSensitivity);
            Assert.Equal(root.PathIdentityKey, updated.PathIdentityKey);
            resolver.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Update_InsensitiveOverrideRequiresIdentityMigrationWorkflow()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var firstPath = FileUtils.GetAbsolutePath("CaseVariantRoot");
            var secondPath = FileUtils.GetAbsolutePath("casevariantroot");
            await using (var db = new ListenArrDbContext(options))
            {
                db.RootFolders.AddRange(
                    new RootFolder { Id = 1, Name = "First", Path = firstPath },
                    new RootFolder { Id = 2, Name = "Second", Path = secondPath });
                await db.SaveChangesAsync();
            }

            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(candidate => candidate.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string path, FileSystemCaseSensitivityMode mode, CancellationToken _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            mode == FileSystemCaseSensitivityMode.Insensitive
                                ? FileSystemCaseSensitivity.Insensitive
                                : FileSystemCaseSensitivity.Sensitive),
                        PathIdentityState.Valid,
                        Path.GetPathRoot(path) ?? path)));
            var service = new RootFolderService(
                new EfRootFolderRepository(
                    new TestDbFactory(options),
                    Mock.Of<ILogger<EfRootFolderRepository>>()),
                null,
                semanticsResolver: resolver.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(new RootFolder
                {
                    Id = 1,
                    Name = "First",
                    Path = firstPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                }));

            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_AllowsFilesystemRootPath()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);
            var filesystemRoot = Path.GetPathRoot(FileUtils.GetAbsolutePath("root"));
            Assert.False(string.IsNullOrWhiteSpace(filesystemRoot));

            var created = await svc.CreateAsync(new RootFolder { Name = "Drive Root", Path = filesystemRoot! });

            Assert.Equal(Path.GetFullPath(filesystemRoot!), created.Path);
        }

        [WindowsFact]
        public async Task Create_AllowsWindowsCurrentDriveRootPath()
        {

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);
            var currentDriveRoot = @"\";

            var created = await svc.CreateAsync(new RootFolder { Name = "Current Drive Root", Path = currentDriveRoot });

            Assert.Equal(Path.GetFullPath(currentDriveRoot), created.Path);
        }

        [Fact]
        public async Task Create_Throws_WhenPathInvalidForCurrentOs()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                svc.CreateAsync(new RootFolder { Name = "Invalid", Path = "relative-root" }));
            Assert.Contains("not valid", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_SkipsInvalidStoredRootWhenCheckingConflicts()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var db = new ListenArrDbContext(options))
            {
                db.RootFolders.Add(new RootFolder { Name = "Stale", Path = "relative-root" });
                await db.SaveChangesAsync();
            }

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);

            var created = await svc.CreateAsync(new RootFolder { Name = "Valid", Path = rootPath });

            Assert.Equal(rootPath, created.Path);
        }

        [Fact]
        public async Task Create_Throws_WhenRootFolderPathContainsParentTraversal()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);
            var parentSegment = new string('.', 2);
            var traversingPath = Path.Join(rootPath, "Audiobooks", parentSegment, "Shared");

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                svc.CreateAsync(new RootFolder { Name = "Traversal Root", Path = traversingPath }));
            Assert.Contains("parent", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_Throws_WhenPathContainsCurrentDirectorySegment()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);
            var rawPath = Path.Join(rootPath, ".");

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                svc.CreateAsync(new RootFolder { Name = "Current Directory Root", Path = rawPath }));

            Assert.Contains("current directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_HandlesTrailingWhitespaceAccordingToCurrentOs()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);
            var pathWithTrailingWhitespace = rootPath + " ";

            if (OperatingSystem.IsWindows())
            {
                var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                    svc.CreateAsync(new RootFolder { Name = "Whitespace Root", Path = pathWithTrailingWhitespace }));
                Assert.Contains("space or period", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var created = await svc.CreateAsync(new RootFolder
                {
                    Name = "Whitespace Root",
                    Path = pathWithTrailingWhitespace
                });
                Assert.Equal(pathWithTrailingWhitespace, created.Path);
            }
        }

        [Fact]
        public async Task Create_Throws_WhenNormalizedPathDuplicate()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var normalizedPath = Path.GetFullPath(rootPath);
            var db = new ListenArrDbContext(options);
            db.RootFolders.Add(new RootFolder { Name = "A", Path = normalizedPath });
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(new RootFolder { Name = "B", Path = rootPath }));
        }

        [Fact]
        public async Task Create_Throws_WhenNestedInsideExistingRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            db.RootFolders.Add(new RootFolder { Name = "Library", Path = rootPath });
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync(new RootFolder { Name = "Nested", Path = Path.Join(rootPath, "Audiobooks") }));

            Assert.Contains("nested", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [LinuxFact]
        public async Task Create_InsensitiveRequestedRootRejectsCaseVariantNestedExistingRoot()
        {

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var existingPath = Path.Join(rootPath, "Library");
            await using (var db = new ListenArrDbContext(options))
            {
                db.RootFolders.Add(new RootFolder
                {
                    Name = "Existing",
                    Path = existingPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                    ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                    PathIdentityState = PathIdentityState.Valid
                });
                await db.SaveChangesAsync();
            }

            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(candidate => candidate.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string path, FileSystemCaseSensitivityMode mode, CancellationToken _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSyntax.Unix,
                            mode == FileSystemCaseSensitivityMode.Insensitive
                                ? FileSystemCaseSensitivity.Insensitive
                                : FileSystemCaseSensitivity.Sensitive),
                        PathIdentityState.Valid,
                        "/")));
            var repository = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(
                repository,
                null,
                semanticsResolver: resolver.Object);
            var requestedPath = Path.Join(rootPath, "library", "Audiobooks");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync(new RootFolder
                {
                    Name = "Requested",
                    Path = requestedPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                }));

            Assert.Contains("nested", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_Throws_WhenRequestedRootContainsExistingRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var nestedRoot = Path.Join(rootPath, "Audiobooks");
            var db = new ListenArrDbContext(options);
            db.RootFolders.Add(new RootFolder { Name = "Nested", Path = nestedRoot });
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync(new RootFolder { Name = "Parent", Path = rootPath }));

            Assert.Contains("contain", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [WindowsFact]
        public async Task Create_Throws_WhenWindowsCaseOnlyDuplicate()
        {

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            db.RootFolders.Add(new RootFolder { Name = "A", Path = rootPath.ToUpperInvariant() });
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync(new RootFolder { Name = "B", Path = rootPath.ToLowerInvariant() }));
        }

        [Fact]
        public async Task Update_Throws_WhenPathInvalidForCurrentOs()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var logger = new TestLogger<RootFolderService>(_output);
            var svc = new RootFolderService(repo, logger);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                svc.UpdateAsync(new RootFolder { Id = root.Id, Name = "R2", Path = "relative-root" }));
            Assert.Contains("not valid", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_Throws_WhenRootFolderPathContainsParentTraversal()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var logger = new TestLogger<RootFolderService>(_output);
            var svc = new RootFolderService(repo, logger);
            var parentSegment = new string('.', 2);
            var traversingPath = Path.Join(rootPath, "Audiobooks", parentSegment, "Shared");

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                svc.UpdateAsync(new RootFolder { Id = root.Id, Name = "R2", Path = traversingPath }));
            Assert.Contains("parent", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_Throws_WhenPathNestedInsideAnotherRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder { Name = "Other", Path = newRootPath });
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(new RootFolder
                {
                    Id = root.Id,
                    Name = "R",
                    Path = Path.Join(newRootPath, "Nested")
                }));

            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_Throws_WhenRequestedRootContainsAnotherRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var nestedRoot = Path.Join(newRootPath, "Nested");
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder { Name = "Nested", Path = nestedRoot });
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(new RootFolder { Id = root.Id, Name = "R", Path = newRootPath }));

            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_AllowsOwnNormalizedEquivalentPath()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = Path.GetFullPath(rootPath) };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            var updated = await service.UpdateAsync(new RootFolder
            {
                Id = root.Id,
                Name = "Renamed",
                Path = rootPath
            });

            Assert.Equal("Renamed", updated.Name);
            Assert.True(FileUtils.AreFilesystemPathsEquivalentForCurrentOs(rootPath, updated.Path));
        }

        [Fact]
        public async Task Delete_UnusedOfflineRoot_DoesNotRequireLiveFilesystemSemantics()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var repository = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var root = new RootFolder
            {
                Name = "Offline Library",
                Path = rootPath,
                CaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey("root", rootPath, semantics),
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = "listenarr-directory-v1:00000000000000000000000000000000:"
                    + new string('0', 64),
                DirectoryObjectIdentityUnavailableReason = "The storage location is currently missing."
            };
            await repository.AddAsync(root);
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
            var service = new RootFolderService(
                repository,
                null,
                semanticsResolver: semanticsResolver.Object);

            await service.DeleteAsync(root.Id);

            Assert.Null(await repository.GetByIdAsync(root.Id));
            semanticsResolver.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Delete_Throws_WhenFilesystemRootHasChildAudiobookWithoutReassign()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var filesystemRoot = Path.GetPathRoot(FileUtils.GetAbsolutePath("root"));
            Assert.False(string.IsNullOrWhiteSpace(filesystemRoot));
            var childAudiobookPath = Path.Join(filesystemRoot!, "Author", "Title");

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "Filesystem Root", Path = Path.GetFullPath(filesystemRoot!) };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "T", BasePath = childAudiobookPath });
            await db.SaveChangesAsync();

            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(root.Id));
        }

        [Fact]
        public async Task Delete_ReassignsFilesystemRootChildAudiobookPreservingRelativePath()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var filesystemRoot = Path.GetPathRoot(FileUtils.GetAbsolutePath("root"));
            Assert.False(string.IsNullOrWhiteSpace(filesystemRoot));
            var childAudiobookPath = Path.Join(filesystemRoot!, "Author", "Title");
            var expectedPath = Path.Join(newRootPath, "Author", "Title");

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "Filesystem Root", Path = Path.GetFullPath(filesystemRoot!) };
            var reassignRoot = new RootFolder { Name = "New Root", Path = newRootPath };
            db.RootFolders.AddRange(root, reassignRoot);
            db.Audiobooks.Add(new Audiobook { Title = "T", BasePath = childAudiobookPath });
            await db.SaveChangesAsync();

            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            await service.DeleteAsync(root.Id, reassignRoot.Id);

            await using var verifyDb = new ListenArrDbContext(options);
            var audiobook = await verifyDb.Audiobooks.SingleAsync();
            Assert.Equal(expectedPath, audiobook.BasePath);
            Assert.DoesNotContain(verifyDb.RootFolders, r => r.Id == root.Id);
        }

        [Fact]
        public async Task DeleteWithReassignment_WaitsForEveryExistingAudiobookOperation()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var sourcePath = Path.Join(rootPath, "Author", "Title");
            var expectedPath = Path.Join(newRootPath, "Author", "Title");
            int unrelatedAudiobookId;
            int sourceRootId;
            int targetRootId;
            await using (var db = new ListenArrDbContext(options))
            {
                var sourceRoot = new RootFolder { Name = "Source", Path = rootPath };
                var targetRoot = new RootFolder { Name = "Target", Path = newRootPath };
                var audiobook = new Audiobook { Title = "Reassigned", BasePath = sourcePath };
                var unrelatedAudiobook = new Audiobook
                {
                    Title = "Unrelated",
                    BasePath = FileUtils.GetAbsolutePath("other-root", "Author", "Title")
                };
                db.RootFolders.AddRange(sourceRoot, targetRoot);
                db.Audiobooks.AddRange(audiobook, unrelatedAudiobook);
                await db.SaveChangesAsync();
                unrelatedAudiobookId = unrelatedAudiobook.Id;
                sourceRootId = sourceRoot.Id;
                targetRootId = targetRoot.Id;
            }

            var repo = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            using var operationCoordinator = new AudiobookOperationCoordinator();
            var service = new RootFolderService(
                repo,
                null!,
                audiobookOperationCoordinator: operationCoordinator);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = operationCoordinator.ExecuteExclusiveAsync(
                unrelatedAudiobookId,
                async _ =>
                {
                    entered.SetResult();
                    await release.Task;
                });
            await entered.Task;

            var deleteTask = service.DeleteAsync(sourceRootId, targetRootId);
            await Task.Delay(50);
            Assert.False(deleteTask.IsCompleted);

            release.SetResult();
            await Task.WhenAll(blocker, deleteTask);

            await using var verification = new ListenArrDbContext(options);
            Assert.Equal(
                expectedPath,
                (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Title == "Reassigned")).BasePath);
            Assert.DoesNotContain(verification.RootFolders, root => root.Id == sourceRootId);
        }

        [Fact]
        public async Task Delete_Throws_WhenReassignmentTargetHasActiveRelocation()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var sourceRoot = new RootFolder { Name = "Source", Path = rootPath };
            var targetRoot = new RootFolder { Name = "Target", Path = newRootPath };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.Add(new Audiobook { Title = "T", BasePath = rootAuthorTitlePath });
            await db.SaveChangesAsync();

            var repo = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.GetActiveForRootAsync(
                    targetRoot.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderRelocation
                {
                    RootFolderId = targetRoot.Id,
                    ActiveRootFolderId = targetRoot.Id,
                    SourcePath = newRootPath,
                    TargetPath = FileUtils.GetAbsolutePath("relocating-target"),
                    Mode = RootFolderRelocationMode.MetadataOnly,
                    Status = RootFolderRelocationStatus.NeedsAttention
                });
            var service = new RootFolderService(
                repo,
                null!,
                relocationService: relocationService.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteAsync(sourceRoot.Id, targetRoot.Id));

            Assert.Contains("relocation", exception.Message, StringComparison.OrdinalIgnoreCase);
            await using var verification = new ListenArrDbContext(options);
            Assert.Equal(2, await verification.RootFolders.CountAsync());
            Assert.Equal(rootAuthorTitlePath, (await verification.Audiobooks.SingleAsync()).BasePath);
        }

        [Fact]
        public async Task Delete_Throws_WhenReferencedWithoutReassign()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "A", Path = booksPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "T", BasePath = booksPath });
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync(root.Id));
        }

        [Fact]
        public async Task Delete_Throws_WhenActiveMoveJobTouchesSourcePathInsideRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(queue => queue.GetFilesystemBlockingJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new MoveJob
                    {
                        SourcePath = Path.Join(rootPath, "Author", "Title"),
                        RequestedPath = Path.Join(newRootPath, "Author", "Title"),
                        Status = MoveJobStatus.Queued
                    }
                ]);
            var service = new RootFolderService(repo, null!, moveQueue.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(root.Id));

            Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Delete_Throws_WhenActiveMoveJobTouchesDestinationPathInsideRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(queue => queue.GetFilesystemBlockingJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new MoveJob
                    {
                        SourcePath = Path.Join(newRootPath, "Author", "Title"),
                        RequestedPath = Path.Join(rootPath, "Author", "Title"),
                        Status = MoveJobStatus.Running
                    }
                ]);
            var service = new RootFolderService(repo, null!, moveQueue.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(root.Id));

            Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Delete_Throws_WhenFailedPublishedMoveStillOwnsRootFilesystemState()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var moveQueue = new Mock<IMoveQueueService>();
            var failedJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                SourcePath = Path.Join(rootPath, "Author", "Title"),
                RequestedPath = Path.Join(newRootPath, "Author", "Title"),
                Status = MoveJobStatus.Failed,
                Phase = MoveJobPhase.Published,
                FailureKind = MoveFailureKind.Unknown
            };
            moveQueue.Setup(queue => queue.GetFilesystemBlockingJobsAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([failedJob]);
            var service = new RootFolderService(repo, null!, moveQueue.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteAsync(root.Id));

            Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
            await using var verification = new ListenArrDbContext(options);
            Assert.Single(verification.RootFolders);
        }

        [Fact]
        public async Task Delete_AllowsCompletedMoveJobTouchingRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(queue => queue.GetFilesystemBlockingJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var service = new RootFolderService(repo, null!, moveQueue.Object);

            await service.DeleteAsync(root.Id);

            await using var verifyDb = new ListenArrDbContext(options);
            Assert.Empty(verifyDb.RootFolders);
        }

        [Fact]
        public async Task Update_Throws_WhenActiveMoveJobTouchesOldRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(queue => queue.GetFilesystemBlockingJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new MoveJob
                    {
                        SourcePath = Path.Join(rootPath, "Author", "Title"),
                        RequestedPath = Path.Join(FileUtils.GetAbsolutePath("elsewhere"), "Author", "Title"),
                        Status = MoveJobStatus.Queued
                    }
                ]);
            var service = new RootFolderService(repo, null!, moveQueue.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(new RootFolder { Id = root.Id, Name = "R", Path = newRootPath }));

            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_Throws_WhenActiveMoveJobTouchesNewRoot()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(new TestDbFactory(options), Mock.Of<ILogger<EfRootFolderRepository>>());
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(queue => queue.GetFilesystemBlockingJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new MoveJob
                    {
                        SourcePath = Path.Join(FileUtils.GetAbsolutePath("elsewhere"), "Author", "Title"),
                        RequestedPath = Path.Join(newRootPath, "Author", "Title"),
                        Status = MoveJobStatus.Running
                    }
                ]);
            var service = new RootFolderService(repo, null!, moveQueue.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(new RootFolder { Id = root.Id, Name = "R", Path = newRootPath }));

            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_RenameWithoutMove_UpdatesAudiobookBasePaths()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "A1", BasePath = rootAuthorTitlePath });
            db.Audiobooks.Add(new Audiobook { Title = "A2", BasePath = rootPath });
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var logger = new TestLogger<RootFolderService>(_output);
            var svc = new RootFolderService(repo, logger);

            using (var pre = new ListenArrDbContext(options))
            {
                var dumpPre = string.Join("; ", pre.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("Before update: " + dumpPre);
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.UpdateAsync(new RootFolder { Id = root.Id, Name = "R2", Path = newRootPath }, moveFiles: false));
            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);

            using (var verifyDb = new ListenArrDbContext(options))
            {
                var dumpAfter = string.Join("; ", verifyDb.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("After update: " + dumpAfter);

                var a1 = verifyDb.Audiobooks.First(a => a.Title == "A1").BasePath;
                var a2 = verifyDb.Audiobooks.First(a => a.Title == "A2").BasePath;
                Assert.Equal(rootAuthorTitlePath, a1);
                Assert.Equal(rootPath, a2);
            }
        }

        [Fact]
        public async Task Update_RenameWithMove_EnqueuesMovesAndUpdatesDB()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            var ab1 = new Audiobook { Id = 1, Title = "A1", BasePath = rootAuthorTitlePath };
            var ab2 = new Audiobook { Id = 2, Title = "A2", BasePath = rootPath };
            db.Audiobooks.AddRange(ab1, ab2);
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());

            var mockMove = new Moq.Mock<IMoveQueueService>();

            var logger = new TestLogger<RootFolderService>(_output);
            var svc = new RootFolderService(repo, logger, mockMove.Object);

            using (var pre = new ListenArrDbContext(options))
            {
                var dumpPre = string.Join("; ", pre.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("Before update (with move): " + dumpPre);
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateAsync(
                new RootFolder { Id = root.Id, Name = "R2", Path = newRootPath },
                moveFiles: true,
                deleteEmptySource: false));
            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);

            using (var verifyDb = new ListenArrDbContext(options))
            {
                var dumpAfter = string.Join("; ", verifyDb.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("After update (with move): " + dumpAfter);

                var a1 = verifyDb.Audiobooks.First(a => a.Title == "A1").BasePath;
                var a2 = verifyDb.Audiobooks.First(a => a.Title == "A2").BasePath;
                Assert.Equal(rootAuthorTitlePath, a1);
                Assert.Equal(rootPath, a2);
            }

            mockMove.Verify(m => m.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [LinuxFact]
        public async Task Update_CaseOnlyRenameOnCaseSensitiveHost_RequiresPathChangeWorkflow()
        {

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var oldRootPath = FileUtils.GetAbsolutePath("case-root");
            var renamedRootPath = FileUtils.GetAbsolutePath("Case-Root");
            var oldAudiobookPath = Path.Join(oldRootPath, "Author", "Title");
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = oldRootPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "A1", BasePath = oldAudiobookPath });
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var service = new RootFolderService(repo, null!);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(new RootFolder
                {
                    Id = root.Id,
                    Name = "R",
                    Path = renamedRootPath
                }));
            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);

            await using var verificationDb = new ListenArrDbContext(options);
            var audiobook = await verificationDb.Audiobooks.SingleAsync();
            Assert.Equal(oldAudiobookPath, audiobook.BasePath);
        }

        [Fact]
        public async Task Update_MoveEnqueueFailure_IsPropagated()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "A1", BasePath = rootAuthorTitlePath });
            await db.SaveChangesAsync();
            var repo = new EfRootFolderRepository(
                new TestDbFactory(options),
                Mock.Of<ILogger<EfRootFolderRepository>>());
            var moveQueue = new Mock<IMoveQueueService>();
            var service = new RootFolderService(repo, new TestLogger<RootFolderService>(_output), moveQueue.Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(
                    new RootFolder { Id = root.Id, Name = "R", Path = newRootPath },
                    moveFiles: true));

            Assert.Contains("path-changes", exception.Message, StringComparison.OrdinalIgnoreCase);
            moveQueue.Verify(queue => queue.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        private static string CreateTempDirectory(string name)
        {
            var path = Path.Join(
                Path.GetTempPath(),
                $"{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static IFileSystemSemanticsResolver BuildSemanticsResolver(
            FileSystemCaseSensitivity caseSensitivity = FileSystemCaseSensitivity.Sensitive)
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, mode, _) =>
                {
                    var resolvedCaseSensitivity = mode == FileSystemCaseSensitivityMode.Insensitive
                        ? FileSystemCaseSensitivity.Insensitive
                        : mode == FileSystemCaseSensitivityMode.Sensitive
                            ? FileSystemCaseSensitivity.Sensitive
                            : caseSensitivity;
                    return ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            resolvedCaseSensitivity),
                        PathIdentityState.Valid,
                        Path.GetPathRoot(path) ?? path));
                });
            return resolver.Object;
        }

        private class TestDbFactory : IDbContextFactory<ListenArrDbContext>
        {
            private readonly DbContextOptions<ListenArrDbContext> _options;
            public TestDbFactory(DbContextOptions<ListenArrDbContext> options) { _options = options; }
            public Task<ListenArrDbContext> CreateDbContextAsync() => Task.FromResult(new ListenArrDbContext(_options));
            public ListenArrDbContext CreateDbContext() => new ListenArrDbContext(_options);
        }

        private class TestLogger<T> : ILogger<T>
        {
            private readonly ITestOutputHelper _out;
            public TestLogger(ITestOutputHelper output) { _out = output; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _out.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception != null ? " Exception: " + exception : "")}");
            }
        }
    }

    internal sealed class RootFolderServiceTestAdapter : AppRootFolderService
    {
        public RootFolderServiceTestAdapter(
            IRootFolderRepository repo,
            ILogger<RootFolderServiceTestAdapter>? logger,
            IMoveQueueService? moveQueue = null,
            IFileSystemSemanticsResolver? semanticsResolver = null,
            IRootFolderRelocationService? relocationService = null,
            IFilesystemMutationCoordinator? mutationCoordinator = null,
            IAudiobookOperationCoordinator? audiobookOperationCoordinator = null,
            IDirectoryObjectIdentityResolver? directoryObjectIdentityResolver = null)
            : base(
                repo,
                logger,
                semanticsResolver ?? BuildSemanticsResolver(),
                moveQueue ?? Mock.Of<IMoveQueueService>(),
                relocationService ?? Mock.Of<IRootFolderRelocationService>(),
                mutationCoordinator ?? new FilesystemMutationCoordinator(),
                audiobookOperationCoordinator ?? new AudiobookOperationCoordinator(),
                directoryObjectIdentityResolver)
        {
        }

        private static IFileSystemSemanticsResolver BuildSemanticsResolver()
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, mode, _) =>
                {
                    var resolvedCaseSensitivity = mode == FileSystemCaseSensitivityMode.Insensitive
                        ? FileSystemCaseSensitivity.Insensitive
                        : mode == FileSystemCaseSensitivityMode.Sensitive
                            ? FileSystemCaseSensitivity.Sensitive
                            : OperatingSystem.IsWindows()
                                ? FileSystemCaseSensitivity.Insensitive
                                : FileSystemCaseSensitivity.Sensitive;
                    return ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            resolvedCaseSensitivity),
                        PathIdentityState.Valid,
                        Path.GetPathRoot(path) ?? path));
                });
            return resolver.Object;
        }
    }
}
