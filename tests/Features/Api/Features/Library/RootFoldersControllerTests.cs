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
using System.Net;
using System.Text;
using System.Text.Json;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Listenarr.Infrastructure.Persistence.Repositories;
using AppRootFoldersController = Listenarr.Api.Features.Library.RootFoldersController;
using RootFoldersController = Listenarr.Tests.Features.Api.Features.Library.RootFoldersControllerTestAdapter;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "RootFoldersControllerTests")]
    [Trait("Category", "Api")]
    public sealed class RootFoldersControllerTests : BaseTests
    {
        private class FakeUnmatchedQueue : IUnmatchedScanQueueService
        {
            public UnmatchedScanJob? LastJob { get; set; }

            public System.Threading.Channels.ChannelReader<UnmatchedScanJob> Reader =>
                System.Threading.Channels.Channel.CreateUnbounded<UnmatchedScanJob>().Reader;
            public Task<Guid> EnqueueAsync(string rootFolderPath) => Task.FromResult(Guid.NewGuid());
            public bool TryGetJob(Guid id, out UnmatchedScanJob? job)
            {
                job = LastJob;
                return job != null && job.Id == id;
            }
            public void UpdateJob(Guid id, string status, List<UnmatchedFileResult>? results = null, string? error = null) { }
            public bool TryGetLastJobForPath(string rootFolderPath, out UnmatchedScanJob? job)
            {
                job = LastJob;
                return job != null && string.Equals(job.RootFolderPath, rootFolderPath, StringComparison.Ordinal);
            }
        }

        private static readonly IUnmatchedScanQueueService _fakeQueue = new FakeUnmatchedQueue();

        internal static IFileSystemSemanticsResolver BuildSemanticsResolver(
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

        private static ListenArrDbContext CreateDb() =>
            new ListenArrDbContext(
                new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);

        private class FakeService : IRootFolderService
        {
            public List<RootFolder> Store { get; } = new List<RootFolder>();
            public bool ThrowPersistenceConflictOnDelete { get; set; }
            public Exception? CreateException { get; set; }

            public Task<RootFolder?> GetDefaultAsync() => Task.FromResult(Store.Count > 0 ? Store.First() : null);

            public Task<List<RootFolder>> GetAllAsync() => Task.FromResult(new List<RootFolder>(Store));

            public Task<RootFolder?> GetByIdAsync(int id)
            {
                var f = Store.Find(s => s.Id == id);
                return Task.FromResult<RootFolder?>(f);
            }

            public Task<RootFolder> CreateAsync(RootFolder root)
            {
                if (CreateException != null)
                {
                    throw CreateException;
                }

                // simulate duplicate path error
                if (Store.Exists(s => string.Equals(s.Path, root.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("A root with the same path already exists")
; root.Id = Store.Count + 1;
                Store.Add(root);
                return Task.FromResult(root);
            }

            public Task<RootFolder> UpdateAsync(RootFolder root, bool moveFiles = false, bool deleteEmptySource = true)
            {
                var idx = Store.FindIndex(s => s.Id == root.Id);
                if (idx < 0) throw new KeyNotFoundException("Root folder not found");

                // simulate invalid operation for certain paths
                if (root.Path?.Contains("/invalid/") == true) throw new InvalidOperationException("Invalid path")
;
                Store[idx] = root;
                return Task.FromResult(root);
            }

            public Task DeleteAsync(int id, int? reassignRootId = null)
            {
                var idx = Store.FindIndex(s => s.Id == id);
                if (idx < 0) throw new KeyNotFoundException("Root folder not found");
                if (ThrowPersistenceConflictOnDelete)
                    throw new DbUpdateException("Delete failed due to relational constraint.", new Exception("FK"));

                // simulate in-use error if path contains "inuse"
                if (Store[idx].Path?.Contains("inuse") == true && reassignRootId == null)
                    throw new InvalidOperationException("Root folder in use")
;
                Store.RemoveAt(idx);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task GetAll_ReturnsAll()
        {
            var createdAt = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
            var updatedAt = createdAt.AddMinutes(5);
            var svc = new FakeService();
            svc.Store.AddRange(new[] {
                new RootFolder
                {
                    Id = 1,
                    Name = "Root1",
                    Path = FileUtils.GetAbsolutePath("root1"),
                    CreatedAt = createdAt,
                    UpdatedAt = null
                },
                new RootFolder
                {
                    Id = 2,
                    Name = "Root2",
                    Path = FileUtils.GetAbsolutePath("root2"),
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt
                }
            });

            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.GetAll();
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(res);
            var list = Assert.IsAssignableFrom<List<RootFolderDto>>(ok.Value);
            Assert.Equal(2, list.Count);
            Assert.All(
                list,
                item => Assert.Equal(
                    OperatingSystem.IsWindows() ? "Windows" : "Unix",
                    item.PathSyntax));
            Assert.Equal(createdAt, list[0].CreatedAt);
            Assert.Null(list[0].UpdatedAt);
            Assert.Equal(createdAt, list[1].CreatedAt);
            Assert.Equal(updatedAt, list[1].UpdatedAt);
        }

        [Fact]
        public async Task GetAll_AmbiguousPersistedRoot_DoesNotExposeBorrowedHostSyntax()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 7,
                Name = "Ambiguous",
                Path = "//server/share/library",
                PathIdentityState = PathIdentityState.Unavailable
            });
            using var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());

            var result = await controller.GetAll();

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var root = Assert.Single(Assert.IsAssignableFrom<List<RootFolderDto>>(ok.Value));
            Assert.Null(root.PathSyntax);
        }

        [Fact]
        public void GetUnmatchedResults_RedactsInternalFailureDetails()
        {
            var queue = new FakeUnmatchedQueue
            {
                LastJob = new UnmatchedScanJob
                {
                    Id = Guid.NewGuid(),
                    RootFolderPath = "C:\\private\\library",
                    Status = "Failed",
                    Error = "C:\\private\\library failed with worker secret",
                    Results =
                    [
                        new UnmatchedFileResult
                        {
                            FullPath = "C:\\private\\library\\book.m4b",
                            RelativePath = "book.m4b"
                        }
                    ]
                }
            };
            using var db = CreateDb();
            var controller = new RootFoldersController(
                new FakeService(),
                queue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());

            var result = controller.GetUnmatchedResults(queue.LastJob.Id);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains("The unmatched scan failed", json, StringComparison.Ordinal);
            Assert.Contains("book.m4b", json, StringComparison.Ordinal);
            Assert.DoesNotContain("worker secret", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void OpenApi_PreservesLegacyPutRouteAndTimestampContract()
        {
            using var factory = new Listenarr.Tests.Mocks.ListenarrWebApplicationFactory();
            var swaggerDocument = factory.Services
                .GetRequiredService<ISwaggerProvider>()
                .GetSwagger("v1");
            using var textWriter = new StringWriter();
            var writer = new OpenApiJsonWriter(textWriter);
            swaggerDocument.SerializeAs(OpenApiSpecVersion.OpenApi3_0, writer);
            using var document = JsonDocument.Parse(textWriter.ToString());
            var root = document.RootElement;
            var rootFolderPath = root
                .GetProperty("paths")
                .GetProperty("/api/v1/rootfolders/{id}");
            var put = rootFolderPath.GetProperty("put");
            var requestSchemaReference = put
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString();
            const string schemaReferencePrefix = "#/components/schemas/";
            Assert.NotNull(requestSchemaReference);
            Assert.StartsWith(schemaReferencePrefix, requestSchemaReference, StringComparison.Ordinal);
            var schemaName = requestSchemaReference[schemaReferencePrefix.Length..];
            Assert.EndsWith(
                $".{nameof(RootFolder)}",
                schemaName,
                StringComparison.Ordinal);

            var schema = root
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty(schemaName);
            var properties = schema.GetProperty("properties");
            var createdAt = properties.GetProperty("createdAt");
            Assert.Equal("string", createdAt.GetProperty("type").GetString());
            Assert.Equal("date-time", createdAt.GetProperty("format").GetString());
            Assert.False(
                createdAt.TryGetProperty("nullable", out var createdAtNullable)
                && createdAtNullable.GetBoolean());

            var updatedAt = properties.GetProperty("updatedAt");
            Assert.Equal("string", updatedAt.GetProperty("type").GetString());
            Assert.Equal("date-time", updatedAt.GetProperty("format").GetString());
            Assert.True(updatedAt.GetProperty("nullable").GetBoolean());

            var createRequestSchemaReference = root
                .GetProperty("paths")
                .GetProperty("/api/v1/rootfolders")
                .GetProperty("post")
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString();
            Assert.Equal(requestSchemaReference, createRequestSchemaReference);
        }

        [Theory]
        [InlineData("\"mode\":\"999\",\"targetCaseSensitivityMode\":\"Auto\"")]
        [InlineData("\"mode\":\"relocate\",\"targetCaseSensitivityMode\":999")]
        public async Task HttpPipeline_RejectsMalformedRelocationEnumsBeforeServiceAccess(
            string enumProperties)
        {
            using var factory = new Listenarr.Tests.Mocks.ListenarrWebApplicationFactory();
            using var client = factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/rootfolders/1/path-changes")
            {
                Content = new StringContent(
                    $$"""
                    {
                      "targetPath": "C:\\library\\target",
                      {{enumProperties}},
                      "deleteEmptySource": false,
                      "desiredName": "Root",
                      "desiredIsDefault": false,
                      "expectedCurrentPath": "C:\\library\\source"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("case sensitivity", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Update_IsExposedAsLegacyPutAction()
        {
            var method = typeof(AppRootFoldersController).GetMethod(nameof(AppRootFoldersController.Update));

            Assert.NotNull(method);
            var put = Assert.Single(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPutAttribute), inherit: true));
            Assert.Equal("{id}", ((Microsoft.AspNetCore.Mvc.HttpPutAttribute)put).Template);
            Assert.Empty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.NonActionAttribute), inherit: true));
        }

        [Fact]
        public async Task Get_NotFound_Returns404()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.Get(123);
            var notFound = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(res);
            Assert.Contains("not found", notFound.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_LegacyRootFolderBody_IgnoresClientManagedIdentityAndTimestamps()
        {
            var svc = new FakeService();
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());
            var clientCreatedAt = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clientUpdatedAt = clientCreatedAt.AddDays(1);

            var result = await controller.Create(new RootFolder
            {
                Id = 999,
                Name = "Legacy Create",
                Path = FileUtils.GetAbsolutePath("legacy-create"),
                IsDefault = true,
                CreatedAt = clientCreatedAt,
                UpdatedAt = clientUpdatedAt
            });

            var created = Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>(result);
            var payload = Assert.IsType<RootFolderDto>(created.Value);
            Assert.Equal(1, payload.Id);
            Assert.NotEqual(clientCreatedAt, payload.CreatedAt);
            Assert.Null(payload.UpdatedAt);
            Assert.True(payload.IsDefault);
        }

        [Fact]
        public async Task Create_DuplicatePath_ReturnsConflict()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R1", Path = FileUtils.GetAbsolutePath("dup") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var req = new RootFolder
            {
                Name = "New",
                Path = FileUtils.GetAbsolutePath("dup"),
                IsDefault = false,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            };
            var res = await controller.Create(req);

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(res);
            Assert.Contains("conflicts", conflict.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_InternalValidationReason_IsRedacted()
        {
            const string secret = "C:\\private\\identity-secret";
            var svc = new FakeService
            {
                CreateException = new InvalidOperationException(
                    $"Filesystem identity resolution failed at {secret}")
            };
            using var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());

            var result = await controller.Create(new RootFolder
            {
                Name = "Secret Root",
                Path = FileUtils.GetAbsolutePath("secret-root")
            });

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
            var json = JsonSerializer.Serialize(conflict.Value);
            Assert.Contains("conflicts with existing configuration", json, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identity resolution", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_IdMismatch_ReturnsBadRequest()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var req = new RootFolder { Id = 2, Name = "R", Path = FileUtils.GetAbsolutePath("p") };
            var res = await controller.Update(1, req);

            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("Id mismatch", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_DistinctPath_UsesDurableMetadataOnlyCompatibilityAdapter()
        {
            var sourcePath = FileUtils.GetAbsolutePath("legacy-source");
            var targetPath = FileUtils.GetAbsolutePath("legacy-target");
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Old Name",
                Path = sourcePath,
                CreatedAt = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    var root = svc.Store.Single();
                    root.Path = command.TargetPath;
                    root.Name = command.DesiredName;
                    root.IsDefault = command.DesiredIsDefault;
                    root.CaseSensitivityMode = command.TargetCaseSensitivityMode;
                    root.UpdatedAt = DateTime.UtcNow;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);
            var request = new RootFolder
            {
                Id = 1,
                Name = "New Name",
                Path = targetPath,
                IsDefault = true,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };

            var result = await controller.Update(
                1,
                request,
                moveFiles: false,
                deleteEmptySource: false);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var payload = Assert.IsType<RootFolderDto>(ok.Value);
            Assert.Equal(targetPath, payload.Path);
            Assert.Equal("New Name", payload.Name);
            Assert.True(payload.IsDefault);
            Assert.NotNull(payload.UpdatedAt);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.TargetPath == targetPath
                    && command.Mode == RootFolderRelocationMode.MetadataOnly
                    && !command.DeleteEmptySource
                    && command.DesiredName == "New Name"
                    && command.DesiredIsDefault
                    && command.TargetCaseSensitivityMode == FileSystemCaseSensitivityMode.Insensitive),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_InvalidPersistedSourceSyntax_StillAllowsMetadataRepairThroughDurableWorkflow()
        {
            var invalidSourcePath = "invalid::legacy-root";
            var targetPath = FileUtils.GetAbsolutePath("legacy-repaired-root");
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Legacy Root",
                Path = invalidSourcePath
            });
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                    invalidSourcePath,
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentException("invalid legacy source"));
            semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                    targetPath,
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Valid,
                    Path.GetPathRoot(targetPath) ?? targetPath));
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    svc.Store.Single().Path = command.TargetPath;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    invalidSourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                semanticsResolver.Object,
                relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Repaired Root",
                    Path = targetPath
                },
                moveFiles: false);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.Equal(targetPath, Assert.IsType<RootFolderDto>(ok.Value).Path);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.Mode == RootFolderRelocationMode.MetadataOnly
                    && command.TargetPath == targetPath),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_DistinctPathWithMoveFiles_ReturnsAcceptedAndUsesRelocateMode()
        {
            var sourcePath = FileUtils.GetAbsolutePath("legacy-physical-source");
            var targetPath = FileUtils.GetAbsolutePath("legacy-physical-target");
            var relocationId = Guid.NewGuid();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderPathChangeResult(
                    relocationId,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Pending,
                    1,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = targetPath
                },
                moveFiles: true,
                deleteEmptySource: true);

            var accepted = Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedAtRouteResult>(result);
            Assert.Equal("GetRootFolderRelocation", accepted.RouteName);
            Assert.Equal(relocationId, accepted.RouteValues!["id"]);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.Mode == RootFolderRelocationMode.Relocate
                    && command.DeleteEmptySource
                    && command.TargetPath == targetPath),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_CaseSensitivityChangeOnEquivalentPath_MigratesIdentitiesAndPreservesCanonicalSpelling()
        {
            var sourcePath = FileUtils.GetAbsolutePath("LegacyCaseRoot");
            var targetPath = sourcePath.ToLowerInvariant();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    var stored = svc.Store.Single();
                    stored.Name = command.DesiredName;
                    stored.IsDefault = command.DesiredIsDefault;
                    stored.CaseSensitivityMode = command.TargetCaseSensitivityMode;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    sourcePath,
                    sourcePath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Renamed",
                    Path = targetPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
                });

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var payload = Assert.IsType<RootFolderDto>(ok.Value);
            Assert.Equal(sourcePath, payload.Path);
            Assert.Equal("Renamed", payload.Name);
            Assert.Equal("Sensitive", payload.CaseSensitivityMode);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.TargetPath == sourcePath
                    && command.Mode == RootFolderRelocationMode.MetadataOnly
                    && command.TargetCaseSensitivityMode == FileSystemCaseSensitivityMode.Sensitive),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_SynchronousNeedsAttention_ReturnsRecoveryResultInsteadOfRootSuccess()
        {
            var sourcePath = FileUtils.GetAbsolutePath("AttentionSourceRoot");
            var targetPath = FileUtils.GetAbsolutePath("AttentionTargetRoot");
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            });
            var relocationId = Guid.NewGuid();
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderPathChangeResult(
                    relocationId,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.NeedsAttention,
                    0,
                    0,
                    $"Internal failure at {targetPath}",
                    TargetIdentityEnrollmentState.Authorized));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Renamed",
                    Path = targetPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                });

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
            var payload = Assert.IsType<RootFolderPathChangeResult>(conflict.Value);
            Assert.Equal(relocationId, payload.RelocationId);
            Assert.Equal(RootFolderRelocationStatus.NeedsAttention, payload.Status);
            Assert.DoesNotContain(targetPath, payload.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("requires attention", payload.Error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Patch_CaseSensitivityChange_RequiresPathChangeEndpoint()
        {
            var sourcePath = FileUtils.GetAbsolutePath("PatchSemanticsRoot");
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Patch(
                1,
                new RootFolderMetadataUpdateRequest(
                    "Renamed",
                    true,
                    FileSystemCaseSensitivityMode.Insensitive));

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
            Assert.Contains(
                "path-changes",
                conflict.Value!.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Root", svc.Store.Single().Name);
            Assert.False(svc.Store.Single().IsDefault);
            Assert.Equal(
                FileSystemCaseSensitivityMode.Sensitive,
                svc.Store.Single().CaseSensitivityMode);
            relocationService.Verify(service => service.StartAsync(
                It.IsAny<int>(),
                It.IsAny<RootFolderPathChangeCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Update_AutoRootUsesPersistedSensitiveResolutionInsteadOfFreshInsensitiveProbe()
        {
            var sourcePath = FileUtils.GetAbsolutePath("PersistedAutoSensitiveRoot");
            var targetPath = sourcePath.ToLowerInvariant();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    svc.Store.Single().Path = command.TargetPath;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                BuildSemanticsResolver(FileSystemCaseSensitivity.Insensitive),
                relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = targetPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                });

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.Equal(targetPath, Assert.IsType<RootFolderDto>(ok.Value).Path);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.TargetPath == targetPath
                    && command.Mode == RootFolderRelocationMode.MetadataOnly),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_CaseOnlyEditOnSensitivePersistedRoot_UsesRelocation()
        {
            var sourcePath = FileUtils.GetAbsolutePath("LegacySensitiveRoot");
            var targetPath = sourcePath.ToLowerInvariant();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    svc.Store.Single().Path = command.TargetPath;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = targetPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                });

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.Equal(targetPath, Assert.IsType<RootFolderDto>(ok.Value).Path);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.TargetPath == targetPath
                    && command.Mode == RootFolderRelocationMode.MetadataOnly),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_NotFound_ReturnsNotFound()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var req = new RootFolder { Id = 99, Name = "R", Path = FileUtils.GetAbsolutePath("p") };
            var res = await controller.Update(99, req);

            var nf = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(res);
            Assert.Contains("not found", nf.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetSavedUnmatched_FiltersUsingResolvedFolderSemantics()
        {
            var rootPath = Path.Join(Path.GetTempPath(), $"saved-unmatched-root-{Guid.NewGuid():N}");
            var resultPath = Path.Join(rootPath, "CaseBook.m4b");
            var trackedPath = Path.Join(rootPath, "casebook.m4b");
            Directory.CreateDirectory(rootPath);
            try
            {
                await File.WriteAllTextAsync(resultPath, "audio");
                var svc = new FakeService();
                svc.Store.Add(new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
                });
                var queue = new FakeUnmatchedQueue
                {
                    LastJob = new UnmatchedScanJob
                    {
                        RootFolderPath = rootPath,
                        Status = "Completed",
                        CompletedAt = DateTime.UtcNow,
                        Results =
                        [
                            new UnmatchedFileResult { FullPath = resultPath }
                        ]
                    }
                };
                var db = CreateDb();
                db.AudiobookFiles.Add(new AudiobookFile { Id = 1, Path = trackedPath, Format = "m4b" });
                await db.SaveChangesAsync();
                var resolver = BuildSemanticsResolver(FileSystemCaseSensitivity.Sensitive);
                var controller = new RootFoldersController(
                    svc,
                    queue,
                    new EfAudiobookFileRepository(db),
                    new AudiobookRepository(db),
                    new LocalFileSystem(),
                    resolver);

                var result = await controller.GetSavedUnmatched(1);

                var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
                var items = ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value);
                var list = Assert.IsAssignableFrom<List<UnmatchedFileResult>>(items);
                var item = Assert.Single(list);
                Assert.Equal(resultPath, item.FullPath);
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public async Task GetSavedUnmatched_FiltersTrackedFilesAfterCanonicalizingPathSyntax()
        {
            var rootPath = Path.Join(Path.GetTempPath(), $"saved-unmatched-canonical-root-{Guid.NewGuid():N}");
            var resultPath = Path.Join(rootPath, "Book", "book.m4b");
            var trackedPath = Path.Join(rootPath, "Book", ".", "book.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            try
            {
                await File.WriteAllTextAsync(resultPath, "audio");
                var svc = new FakeService();
                svc.Store.Add(new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
                });
                var queue = new FakeUnmatchedQueue
                {
                    LastJob = new UnmatchedScanJob
                    {
                        RootFolderPath = rootPath,
                        Status = "Completed",
                        CompletedAt = DateTime.UtcNow,
                        Results =
                        [
                            new UnmatchedFileResult { FullPath = resultPath }
                        ]
                    }
                };
                var db = CreateDb();
                db.AudiobookFiles.Add(new AudiobookFile { Id = 1, Path = trackedPath, Format = "m4b" });
                await db.SaveChangesAsync();
                var controller = new RootFoldersController(
                    svc,
                    queue,
                    new EfAudiobookFileRepository(db),
                    new AudiobookRepository(db),
                    new LocalFileSystem());

                var result = await controller.GetSavedUnmatched(1);

                var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
                var items = ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value);
                var list = Assert.IsAssignableFrom<List<UnmatchedFileResult>>(items);
                Assert.Empty(list);
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Theory]
        [InlineData("999")]
        [InlineData("-1")]
        [InlineData("0")]
        [InlineData("1")]
        [InlineData("Relocate, MetadataOnly")]
        [InlineData(" relocate ")]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("unknown")]
        [InlineData(null)]
        public async Task ChangePath_InvalidMode_RejectsBeforeRelocation(string? mode)
        {
            var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
            var db = CreateDb();
            var controller = new RootFoldersController(
                new FakeService(),
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);
            var request = new RootFolderPathChangeRequest(
                FileUtils.GetAbsolutePath("invalid-mode-target"),
                mode!,
                false,
                "Root",
                false,
                FileSystemCaseSensitivityMode.Auto,
                FileUtils.GetAbsolutePath("current-root"));

            var result = await controller.ChangePath(1, request, CancellationToken.None);

            var badRequest = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.Contains("Mode", badRequest.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            relocationService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ChangePath_UndefinedCaseSensitivity_RejectsBeforeRelocation()
        {
            var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
            var db = CreateDb();
            var controller = new RootFoldersController(
                new FakeService(),
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);
            var request = new RootFolderPathChangeRequest(
                FileUtils.GetAbsolutePath("invalid-case-sensitivity-target"),
                "relocate",
                false,
                "Root",
                false,
                (FileSystemCaseSensitivityMode)999,
                FileUtils.GetAbsolutePath("current-root"));

            var result = await controller.ChangePath(1, request, CancellationToken.None);

            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            relocationService.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public async Task ChangePath_MissingExpectedCurrentPath_RejectsBeforeRelocation(
            string? expectedCurrentPath)
        {
            var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
            var db = CreateDb();
            var controller = new RootFoldersController(
                new FakeService(),
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);
            var request = new RootFolderPathChangeRequest(
                FileUtils.GetAbsolutePath("missing-expected-target"),
                "relocate",
                false,
                "Root",
                false,
                FileSystemCaseSensitivityMode.Auto,
                expectedCurrentPath!);

            var result = await controller.ChangePath(1, request, CancellationToken.None);

            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            relocationService.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(RootFolderRelocationStatus.Completed, typeof(Microsoft.AspNetCore.Mvc.OkObjectResult))]
        [InlineData(RootFolderRelocationStatus.NeedsAttention, typeof(Microsoft.AspNetCore.Mvc.ConflictObjectResult))]
        public async Task ChangePath_RelocateTerminalResult_UsesTerminalHttpStatus(
            RootFolderRelocationStatus status,
            Type expectedResultType)
        {
            var relocationId = Guid.NewGuid();
            var sourcePath = FileUtils.GetAbsolutePath("terminal-relocate-source");
            var targetPath = FileUtils.GetAbsolutePath("terminal-relocate-target");
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderPathChangeResult(
                    relocationId,
                    1,
                    sourcePath,
                    targetPath,
                    status,
                    0,
                    0,
                    status == RootFolderRelocationStatus.NeedsAttention
                        ? $"Internal failure at {targetPath}"
                        : null,
                    TargetIdentityEnrollmentState.Authorized));
            var db = CreateDb();
            var controller = new RootFoldersController(
                new FakeService(),
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);
            var request = new RootFolderPathChangeRequest(
                targetPath,
                "relocate",
                false,
                "Root",
                false,
                FileSystemCaseSensitivityMode.Auto,
                sourcePath);

            var result = await controller.ChangePath(1, request, CancellationToken.None);

            Assert.IsType(expectedResultType, result);
            var value = result switch
            {
                Microsoft.AspNetCore.Mvc.OkObjectResult ok => ok.Value,
                Microsoft.AspNetCore.Mvc.ConflictObjectResult conflict => conflict.Value,
                _ => null
            };
            var payload = Assert.IsType<RootFolderPathChangeResult>(value);
            Assert.Equal(status, payload.Status);
            if (status == RootFolderRelocationStatus.NeedsAttention)
            {
                Assert.DoesNotContain(targetPath, payload.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("requires attention", payload.Error!, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Theory]
        [InlineData("relocate", RootFolderRelocationMode.Relocate)]
        [InlineData("RELOCATE", RootFolderRelocationMode.Relocate)]
        [InlineData("metadataOnly", RootFolderRelocationMode.MetadataOnly)]
        [InlineData("METADATAONLY", RootFolderRelocationMode.MetadataOnly)]
        public async Task ChangePath_SupportedMode_UsesExactPublicMapping(
            string mode,
            RootFolderRelocationMode expectedMode)
        {
            var relocationId = Guid.NewGuid();
            var targetPath = FileUtils.GetAbsolutePath("valid-mode-target");
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderPathChangeResult(
                    expectedMode == RootFolderRelocationMode.Relocate ? relocationId : null,
                    1,
                    FileUtils.GetAbsolutePath("valid-mode-source"),
                    targetPath,
                    expectedMode == RootFolderRelocationMode.Relocate
                        ? RootFolderRelocationStatus.Pending
                        : RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                new FakeService(),
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);
            var request = new RootFolderPathChangeRequest(
                targetPath,
                mode,
                false,
                "Root",
                false,
                FileSystemCaseSensitivityMode.Auto,
                FileUtils.GetAbsolutePath("valid-mode-source"));

            var result = await controller.ChangePath(1, request, CancellationToken.None);

            if (expectedMode == RootFolderRelocationMode.Relocate)
            {
                Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedAtRouteResult>(result);
            }
            else
            {
                Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            }

            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.Mode == expectedMode
                    && command.TargetCaseSensitivityMode == FileSystemCaseSensitivityMode.Auto
                    && command.ExpectedCurrentPath == FileUtils.GetAbsolutePath("valid-mode-source")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Theory]
        [InlineData("create")]
        [InlineData("update")]
        [InlineData("patch")]
        public async Task RootFolderMutation_UndefinedCaseSensitivity_RejectsBeforeServiceAccess(
            string operation)
        {
            var service = new Mock<IRootFolderService>(MockBehavior.Strict);
            var db = CreateDb();
            var controller = new RootFoldersController(
                service.Object,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());
            var root = new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = FileUtils.GetAbsolutePath("invalid-root-case-sensitivity"),
                CaseSensitivityMode = (FileSystemCaseSensitivityMode)999
            };

            var result = operation switch
            {
                "create" => await controller.Create(root),
                "update" => await controller.Update(1, root),
                "patch" => await controller.Patch(
                    1,
                    new RootFolderMetadataUpdateRequest(
                        root.Name,
                        root.IsDefault,
                        root.CaseSensitivityMode)),
                _ => throw new InvalidOperationException($"Unknown operation {operation}")
            };

            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Delete_InUseWithoutReassign_ReturnsBadRequest()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("inuse") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.Delete(1, null);
            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("in use", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Delete_WithReassign_Succeeds()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("inuse") });
            svc.Store.Add(new RootFolder { Id = 2, Name = "R2", Path = FileUtils.GetAbsolutePath("r") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.Delete(1, 2);
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(res);
            Assert.Contains("Deleted", ok.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
        {
            using var tokenResponse = await client.GetAsync("/api/v1/antiforgery/token");
            tokenResponse.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(
                await tokenResponse.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            return token!;
        }

        [Fact]
        public async Task ConfirmCurrentFolder_ConfirmedGeneration_ReturnsRoot()
        {
            var path = FileUtils.GetAbsolutePath("confirm-root");
            const string confirmationToken = "token";
            var svc = new FakeService();
            var confirmedRoot = new RootFolder { Id = 1, Name = "R", Path = path };
            svc.Store.Add(confirmedRoot);
            var confirmationService = new Mock<IRootFolderStorageConfirmationService>(MockBehavior.Strict);
            confirmationService
                .Setup(service => service.ConfirmCurrentFolderAsync(
                    1,
                    path,
                    confirmationToken,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(confirmedRoot);
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                storageConfirmationService: confirmationService.Object);

            var result = await controller.ConfirmCurrentFolder(
                1,
                new RootFolderConfirmationRequest(path, confirmationToken),
                CancellationToken.None);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var root = Assert.IsType<RootFolderDto>(ok.Value);
            Assert.Equal(1, root.Id);
            Assert.Equal(path, root.Path);
            confirmationService.VerifyAll();
        }

        [Fact]
        public async Task ConfirmCurrentFolder_BlockedState_ReturnsConflictCode()
        {
            var path = FileUtils.GetAbsolutePath("confirm-blocked-root");
            const string confirmationToken = "token";
            var svc = new FakeService();
            var confirmationService = new Mock<IRootFolderStorageConfirmationService>(MockBehavior.Strict);
            confirmationService
                .Setup(service => service.ConfirmCurrentFolderAsync(
                    1,
                    path,
                    confirmationToken,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("blocked"));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                storageConfirmationService: confirmationService.Object);

            var result = await controller.ConfirmCurrentFolder(
                1,
                new RootFolderConfirmationRequest(path, confirmationToken),
                CancellationToken.None);

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
            var json = JsonSerializer.Serialize(conflict.Value);
            Assert.Contains("root_folder_confirmation_blocked", json, StringComparison.Ordinal);
            confirmationService.VerifyAll();
        }

        [Theory]
        [InlineData("", "token")]
        [InlineData("path", "")]
        public async Task ConfirmCurrentFolder_MissingConfirmationData_ReturnsBadRequest(
            string expectedPath,
            string confirmationToken)
        {
            var svc = new FakeService();
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());

            var result = await controller.ConfirmCurrentFolder(
                1,
                new RootFolderConfirmationRequest(expectedPath, confirmationToken),
                CancellationToken.None);

            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Delete_PersistenceConflict_ReturnsConflict()
        {
            var svc = new FakeService
            {
                ThrowPersistenceConflictOnDelete = true
            };
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("delete-conflict") });
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());

            var result = await controller.Delete(1, null);

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
            Assert.Contains("persisted references", conflict.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class RootFoldersControllerTestAdapter : AppRootFoldersController
    {
        public RootFoldersControllerTestAdapter(
            IRootFolderService service,
            IUnmatchedScanQueueService unmatchedQueue,
            IAudiobookFileRepository fileRepository,
            IAudiobookRepository audiobookRepository,
            IFileSystem fileSystem,
            IFileSystemSemanticsResolver? semanticsResolver = null,
            IRootFolderRelocationService? relocationService = null,
            IRootFolderStorageHealthResolver? storageHealthResolver = null,
            IRootFolderStorageConfirmationService? storageConfirmationService = null)
            : base(
                service,
                unmatchedQueue,
                fileRepository,
                audiobookRepository,
                fileSystem,
                semanticsResolver ?? RootFoldersControllerTests.BuildSemanticsResolver(),
                relocationService ?? Mock.Of<IRootFolderRelocationService>(),
                storageHealthResolver ?? new HealthyStorageResolver(),
                storageConfirmationService ?? Mock.Of<IRootFolderStorageConfirmationService>())
        {
        }

        private sealed class HealthyStorageResolver : IRootFolderStorageHealthResolver
        {
            public Task<RootFolderStorageObservation> ResolveAsync(
                RootFolder root,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new RootFolderStorageObservation(
                    RootFolderStorageState.Healthy,
                    RootFolderStorageReason.None,
                    null,
                    CanConfirmCurrentFolder: false,
                    CanChangePath: true,
                    CanMutateFilesystem: true,
                    ConfirmationToken: null));
        }
    }
}
