using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportMarkerlessRegistrationTests")]
[Trait("Category", "Api")]
public sealed class ManualImportMarkerlessRegistrationTests : BaseTests
{
    private readonly Mock<IMetadataService> _metadata = new();

    public ManualImportMarkerlessRegistrationTests()
    {
        var metadata = _metadata;
        metadata.Setup(service => service.ExtractFileMetadataAsync(
                It.IsAny<string>()))
            .ReturnsAsync(new AudioMetadata
            {
                Title = "Manual Markerless",
                Format = "mp3",
                BitRate = 128000
            });
        metadata.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(new AudioMetadata { Format = "mp3", Duration = TimeSpan.FromSeconds(1) });
        Init(builder => builder.WithSingleton(metadata.Object));
    }

    [Theory]
    [InlineData(FileAction.Move, false, false, false)]
    [InlineData(FileAction.Copy, false, false, false)]
    [InlineData(FileAction.HardlinkCopy, false, false, false)]
    [InlineData(FileAction.Move, true, false, false)]
    [InlineData(FileAction.Move, true, true, false)]
    [InlineData(FileAction.Move, true, false, true)]
    public async Task Start_JournalBackedAsinImport_DefersEnrichmentAndRetryConsumesOriginalReceipt(
        FileAction action, bool unavailableEmptySourceSemantics, bool removeEmptySource, bool replaceDestination)
    {
        var outputRoot = FileService.GetTempDirectory("manual-journal-asin-output");
        var sourceRoot = FileService.GetTempDirectory("manual-journal-asin-source");
        var source = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "original-audio");
        if (unavailableEmptySourceSemantics)
        {
            var originalResolver = _provider.GetRequiredService<IFileSystemSemanticsResolver>();
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(service => service.ResolveAsync(It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(), It.IsAny<CancellationToken>()))
                .Returns((string path, FileSystemCaseSensitivityMode mode, CancellationToken token) =>
                    path == sourceRoot && (!Directory.Exists(sourceRoot) || !Directory.EnumerateFileSystemEntries(sourceRoot).Any())
                        ? ValueTask.FromResult(new FileSystemSemanticsResolution(
                            default, PathIdentityState.Unavailable, path, "Empty source case rules unavailable."))
                        : originalResolver.ResolveAsync(path, mode, token));
            Init(builder => builder.WithSingleton(_metadata.Object).WithSingleton(resolver.Object));
        }
        await AddAuthorizedRootAsync(outputRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var book = await _audiobookRepository.AddAsync(new Listenarr.Tests.Builders.AudiobookBuilder()
            .WithTitle("Tagged Book").WithBasePath(outputRoot).WithAsin("B012345678").Build());
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = action,
            Items = [new ManualImportItemDto { FullPath = source, MatchedAudiobookId = book.Id }]
        };
        _metadata.Setup(service => service.WriteAsinTagAsync(
            It.IsAny<IAudiobookFileRegistrationLease>(), book.Asin))
            .Callback<IAudiobookFileRegistrationLease, string>((lease, _) =>
            {
                using var stream = lease.OpenMetadataWriteStream();
                stream.SetLength(0);
                stream.Write(System.Text.Encoding.UTF8.GetBytes("enriched-audio"));
            }).Returns(Task.CompletedTask);

        var first = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await
            ActivatorUtilities.CreateInstance<ManualImportController>(_provider).Start(request)).Result);
        var firstPayload = System.Text.Json.JsonSerializer.SerializeToElement(first.Value);
        Assert.Equal(1, firstPayload.GetProperty("importedCount").GetInt32());
        Assert.Contains("ASIN tagging was deferred", firstPayload.GetProperty("results")[0]
            .GetProperty("Warning").GetString());
        if (removeEmptySource) Directory.Delete(sourceRoot);
        if (replaceDestination)
        {
            var original = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
            await File.WriteAllTextAsync(original.Path!, "replacement-content");
            var refused = (await ActivatorUtilities.CreateInstance<ManualImportController>(_provider).Start(request)).Result;
            Assert.False(refused is Microsoft.AspNetCore.Mvc.OkObjectResult ok
                && System.Text.Json.JsonSerializer.SerializeToElement(ok.Value).GetProperty("importedCount").GetInt32() > 0);
            Assert.Equal("replacement-content", await File.ReadAllTextAsync(original.Path!));
            Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
        }
        else
        {
            var second = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await
                ActivatorUtilities.CreateInstance<ManualImportController>(_provider).Start(request)).Result);
            Assert.Equal(1, System.Text.Json.JsonSerializer.SerializeToElement(second.Value)
                .GetProperty("importedCount").GetInt32());
            var tracked = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
            Assert.Equal("original-audio", await File.ReadAllTextAsync(tracked.Path!));
        }
        Assert.Single(Directory.GetFiles(outputRoot, "*.mp3", SearchOption.AllDirectories));
        _metadata.Verify(service => service.WriteAsinTagAsync(
            It.IsAny<IAudiobookFileRegistrationLease>(), book.Asin), Times.Never);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_CompletedMoveAndNewItem_RecoversOneAndPublishesOnlyNewSource(bool differentAudiobook)
    {
        var output = FileService.GetTempDirectory("mixed-retry-output");
        var source = FileService.GetTempDirectory("mixed-retry-source");
        await AddAuthorizedRootAsync(output);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = output;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.MultiFileNamingPattern = "{Title}-{DiskNumber:00}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Mixed Retry").WithBasePath(output).Build());
        var firstSource = await FileService.GetFileAsync(source, "Part 1.mp3", "first-audio");
        var request = new ManualImportRequestDto
        {
            Path = source,
            Action = FileAction.Move,
            Mode = "interactive",
            IncludeCompanionFiles = true,
            Items = [new ManualImportItemDto { FullPath = firstSource, MatchedAudiobookId = book.Id }]
        };
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(_provider);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await controller.Start(request)).Result);
        var original = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
        var nextSource = await FileService.GetFileAsync(source, "Part 2.mp3", "second-audio");
        var nextBook = differentAudiobook
            ? await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Different Book").WithBasePath(output).Build())
            : book;
        var companion = await FileService.GetFileAsync(source, "metadata.json", "companion");
        request.Items.Add(new ManualImportItemDto { FullPath = nextSource, MatchedAudiobookId = nextBook.Id });

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await controller.Start(request)).Result);
        Assert.Equal(2, System.Text.Json.JsonSerializer.SerializeToElement(response.Value).GetProperty("importedCount").GetInt32());
        var registered = (await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id)).ToList();
        if (differentAudiobook) registered.AddRange(await _audiobookFileRepository.GetByAudiobookIdAsync(nextBook.Id));
        Assert.Equal(2, registered.Count);
        Assert.Contains(registered, file => file.Id == original.Id && file.Path == original.Path);
        Assert.Equal("first-audio", await File.ReadAllTextAsync(original.Path!));
        Assert.Equal("second-audio", await File.ReadAllTextAsync(Assert.Single(registered, file => file.Id != original.Id).Path!));
        Assert.Equal(differentAudiobook, File.Exists(companion));
        Assert.Equal(!differentAudiobook, File.Exists(Path.Join(output, "metadata.json")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Start_WhileOrganiseWaits_PreservesBothPublicationsAndReleasesGate(
        bool cancelOrganise,
        bool sameAudiobook)
    {
        var errors = new List<Exception>();
        var logger = new Mock<ILogger<RenameService>>();
        logger.Setup(service => service.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                if (invocation.Arguments[3] is Exception exception)
                {
                    errors.Add(exception);
                }
            }));
        Init(builder => builder.WithSingleton<IMetadataService>(_metadata.Object).WithSingleton(logger.Object));
        var outputRoot = FileService.GetTempDirectory("manual-organise-overlap-out");
        var sourceRoot = FileService.GetTempDirectory("manual-organise-overlap-src");
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "import audio");
        var oldPath = await FileService.GetFileAsync(outputRoot, "old.mp3", "organise audio");
        await AddAuthorizedRootAsync(outputRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var importBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Imported Book",
            Authors = ["Author"],
            BasePath = outputRoot
        });
        var organiseBook = sameAudiobook ? importBook : await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Organised Book",
            Authors = ["Author"],
            BasePath = outputRoot
        });
        Assert.True(await _provider.GetRequiredService<IAudiobookFileService>()
            .EnsureAudiobookFileAsync(organiseBook, oldPath));
        using var renameScope = _provider.CreateScope();
        var rename = renameScope.ServiceProvider.GetRequiredService<IRenameService>();
        var preview = Assert.Single(await rename.PreviewRenameAsync([organiseBook.Id]));
        var filePreview = Assert.Single(preview.FileRenames);
        var operations = new List<RenameOperation>
        {
            new()
            {
                AudiobookId = organiseBook.Id,
                CurrentFolderPath = preview.CurrentFolderPath,
                CurrentFolderSemantics = preview.CurrentFolderSemantics,
                FileRenames = [new FileRenameOperation
                {
                    FileId = filePreview.FileId, CurrentPath = filePreview.CurrentPath!,
                    NewPath = filePreview.NewPath!
                }]
            }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadata.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return new AudioMetadata { Format = "mp3", Duration = TimeSpan.FromSeconds(1) };
            });
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(_provider);
        var import = controller.Start(new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = importBook.Id }]
        });
        using var cancellation = new CancellationTokenSource();
        Task<List<RenameResult>>? organise = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            organise = rename.ExecuteRenameAsync(operations, cancellation.Token);
            Assert.False(organise.IsCompleted);
            Assert.Equal("organise audio", await File.ReadAllTextAsync(oldPath));
            Assert.Equal(sameAudiobook, File.Exists(filePreview.NewPath));
            if (cancelOrganise)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => organise);
                Assert.Equal("organise audio", await File.ReadAllTextAsync(oldPath));
                Assert.Equal(sameAudiobook, File.Exists(filePreview.NewPath));
            }
        }
        finally
        {
            release.TrySetResult();
            await import.WaitAsync(TimeSpan.FromSeconds(15));
            if (organise != null)
            {
                try
                {
                    await organise.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
        }
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await import).Result);
        Assert.Equal(1, Assert.IsType<int>(ok.Value!.GetType().GetProperty("importedCount")!.GetValue(ok.Value)));
        if (cancelOrganise)
        {
            organise = rename.ExecuteRenameAsync(operations);
        }
        var result = Assert.Single(await organise!.WaitAsync(TimeSpan.FromSeconds(15)));
        if (sameAudiobook)
        {
            // Import occupied this preview's destination while Organise waited.
            // Reject the stale request without overwriting either owned file.
            Assert.False(result.Success);
            Assert.Equal("organise audio", await File.ReadAllTextAsync(oldPath));
            Assert.Equal("import audio", await File.ReadAllTextAsync(filePreview.NewPath!));
            operations[0].FileRenames[0].NewPath = Path.Join(outputRoot, "Organised Book.mp3");
            result = Assert.Single(await rename.ExecuteRenameAsync(operations));
        }
        Assert.True(result.Success, result.Error + Environment.NewLine + string.Join(Environment.NewLine, errors));
        Assert.Equal("import audio", await File.ReadAllTextAsync(Path.Join(outputRoot, "Imported Book.mp3")));
        var organisedPath = operations[0].FileRenames[0].NewPath;
        Assert.Equal("organise audio", await File.ReadAllTextAsync(organisedPath));
        var importedOwner = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(importBook.Id),
            file => file.Path == Path.Join(outputRoot, "Imported Book.mp3"));
        var organisedOwner = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(organiseBook.Id),
            file => file.Path == organisedPath);
        Assert.Equal(sameAudiobook ? 2 : 1,
            (await _audiobookFileRepository.GetByAudiobookIdAsync(organiseBook.Id)).Count);
        Assert.Equal(Path.Join(outputRoot, "Imported Book.mp3"), importedOwner.Path);
        Assert.Equal(organisedPath, organisedOwner.Path);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var importJournal = await db.FileMutationJournals.AsNoTracking().SingleAsync();
        Assert.Equal(importBook.Id, importJournal.AudiobookId);
        Assert.Equal(importedOwner.Path, importJournal.DestinationPath);
        Assert.Equal(FileMutationJournalState.Completed, importJournal.State);
        var renameJournal = await db.VerifiedFileRenameJournals.AsNoTracking().SingleAsync();
        Assert.Equal(organiseBook.Id, renameJournal.AudiobookId);
        Assert.Equal(organisedOwner.Id, renameJournal.AudiobookFileId);
        Assert.Equal(organisedPath, renameJournal.DestinationPath);
        Assert.Equal(VerifiedFileRenameState.Completed, renameJournal.State);
        AssertNoListenarrArtifacts(sourceRoot);
        AssertNoListenarrArtifacts(outputRoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Start_WhileOrganisePublishes_WaitsWithoutMutatingSourceAndRetriesAfterCancellation(
        bool cancelImport,
        bool sameAudiobook)
    {
        var outputRoot = FileService.GetTempDirectory("organise-manual-overlap-out");
        var sourceRoot = FileService.GetTempDirectory("organise-manual-overlap-src");
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "import audio");
        var oldPath = await FileService.GetFileAsync(outputRoot, "old.mp3", "organise audio");
        await AddAuthorizedRootAsync(outputRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var organiseBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Organised Book",
            Authors = ["Author"],
            BasePath = outputRoot
        });
        var importBook = sameAudiobook ? organiseBook : await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Imported Book",
            Authors = ["Author"],
            BasePath = outputRoot
        });
        Assert.True(await _provider.GetRequiredService<IAudiobookFileService>()
            .EnsureAudiobookFileAsync(organiseBook, oldPath));
        using var renameScope = _provider.CreateScope();
        var rename = renameScope.ServiceProvider.GetRequiredService<IRenameService>();
        var coordinator = Assert.IsType<VerifiedFileRenameTransactionCoordinator>(
            renameScope.ServiceProvider.GetRequiredService<IVerifiedFileRenameTransactionCoordinator>());
        var preview = Assert.Single(await rename.PreviewRenameAsync([organiseBook.Id]));
        var filePreview = Assert.Single(preview.FileRenames);
        var operations = new List<RenameOperation>
        {
            new()
            {
                AudiobookId = organiseBook.Id,
                CurrentFolderPath = preview.CurrentFolderPath,
                CurrentFolderSemantics = preview.CurrentFolderSemantics,
                FileRenames = [new FileRenameOperation
                {
                    FileId = filePreview.FileId, CurrentPath = filePreview.CurrentPath!,
                    NewPath = filePreview.NewPath!
                }]
            }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.AfterTargetPublicationForTest = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var organise = Task.Run(() => rename.ExecuteRenameAsync(operations));
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(_provider);
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = importBook.Id }]
        };
        using var cancellation = new CancellationTokenSource();
        Task<Microsoft.AspNetCore.Mvc.ActionResult<object>>? import = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            import = controller.Start(request, cancellation.Token);
            Assert.False(import.IsCompleted);
            Assert.Equal("import audio", await File.ReadAllTextAsync(incoming));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var pausedDb = await factory.CreateDbContextAsync();
            Assert.Empty(await pausedDb.FileMutationJournals.AsNoTracking().ToListAsync());
            Assert.Equal(oldPath, (await pausedDb.AudiobookFiles.AsNoTracking()
                .SingleAsync(file => file.AudiobookId == organiseBook.Id)).Path);
            if (cancelImport)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
                Assert.Equal("import audio", await File.ReadAllTextAsync(incoming));
            }
        }
        finally
        {
            release.TrySetResult();
            await organise.WaitAsync(TimeSpan.FromSeconds(15));
            if (import != null)
            {
                try
                {
                    await import.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
            coordinator.AfterTargetPublicationForTest = null;
        }
        var renameResult = Assert.Single(await organise);
        Assert.True(renameResult.Success, renameResult.Error);
        if (cancelImport)
        {
            import = controller.Start(request);
        }
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await import!).Result);
        Assert.Equal(1, Assert.IsType<int>(ok.Value!.GetType().GetProperty("importedCount")!.GetValue(ok.Value)));
        var verificationFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await verificationFactory.CreateDbContextAsync();
        var importJournal = await db.FileMutationJournals.AsNoTracking().SingleAsync();
        Assert.Equal(importBook.Id, importJournal.AudiobookId);
        Assert.Equal(FileMutationJournalState.Completed, importJournal.State);
        Assert.NotEqual(filePreview.NewPath, importJournal.DestinationPath);
        Assert.Equal("import audio", await File.ReadAllTextAsync(importJournal.DestinationPath));
        Assert.Equal("organise audio", await File.ReadAllTextAsync(filePreview.NewPath!));
        var importedOwner = await db.AudiobookFiles.AsNoTracking().SingleAsync(file =>
            file.AudiobookId == importBook.Id && file.Path == importJournal.DestinationPath);
        var organisedOwner = await db.AudiobookFiles.AsNoTracking().SingleAsync(file =>
            file.AudiobookId == organiseBook.Id && file.Path == filePreview.NewPath);
        Assert.NotEqual(importedOwner.Id, organisedOwner.Id);
        Assert.Equal(sameAudiobook ? 2 : 1,
            await db.AudiobookFiles.CountAsync(file => file.AudiobookId == organiseBook.Id));
        var renameJournal = await db.VerifiedFileRenameJournals.AsNoTracking().SingleAsync();
        Assert.Equal(organisedOwner.Id, renameJournal.AudiobookFileId);
        Assert.Equal(VerifiedFileRenameState.Completed, renameJournal.State);
        AssertNoListenarrArtifacts(sourceRoot);
        AssertNoListenarrArtifacts(outputRoot);
    }

    [Fact]
    public async Task Start_Move_UsesMarkerlessRegistrationJournalWithoutLibraryArtifacts()
    {
        var outputRoot = FileService.GetTempDirectory("manual-markerless-out");
        var sourceRoot = FileService.GetTempDirectory("manual-markerless-src");
        var sourceFile = await FileService.GetFileAsync(
            sourceRoot,
            "incoming.mp3",
            "manual markerless audio");
        await AddAuthorizedRootAsync(outputRoot);

        var settings = await _applicationSettingsRepository.GetAsync()
            ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);

        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Manual Markerless",
            Authors = ["Author"],
            BasePath = outputRoot
        });
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(
            _provider);
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items =
            [
                new ManualImportItemDto
                {
                    FullPath = sourceFile,
                    MatchedAudiobookId = audiobook.Id
                }
            ]
        };

        var action = await controller.Start(request);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            action.Result);
        Assert.Equal(
            1,
            Assert.IsType<int>(ok.Value!.GetType()
                .GetProperty("importedCount")!
                .GetValue(ok.Value)));
        Assert.False(File.Exists(sourceFile));
        var destination = Path.Join(outputRoot, "Manual Markerless.mp3");
        Assert.Equal(
            "manual markerless audio",
            await File.ReadAllTextAsync(destination));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(FileAction.Move, journal.Action);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Equal(audiobook.Id, journal.AudiobookId);
        Assert.Equal(Path.GetFullPath(sourceFile), journal.SourcePath);
        Assert.Equal(Path.GetFullPath(destination), journal.DestinationPath);

        AssertNoListenarrArtifacts(sourceRoot);
        AssertNoListenarrArtifacts(outputRoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Start_WhileMetadataRelocationCommits_UsesCurrentRootAfterWaiting(
        bool cancelImport, bool sameRoot)
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "relocation-import.db")};Pooling=False")
            .Options;
        var factory = new RelocationImportSqliteFactory(options);
        Init(builder => builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext()),
                ServiceDescriptor.Singleton<IRootFolderRelocationService>(provider =>
                    ActivatorUtilities.CreateInstance<RootFolderRelocationService>(provider))));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        var oldRoot = FileService.GetTempDirectory("relocation-import-old");
        var newRoot = FileService.GetTempDirectory("relocation-import-new");
        var otherRoot = FileService.GetTempDirectory("relocation-import-other");
        var sourceRoot = FileService.GetTempDirectory("relocation-import-source");
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "import audio");
        var root = await AddAuthorizedRootAsync(oldRoot);
        await AddAuthorizedRootAsync(otherRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = oldRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var book = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Imported Book",
            Authors = ["Author"],
            BasePath = sameRoot ? oldRoot : otherRoot
        });
        var relocation = Assert.IsType<RootFolderRelocationService>(
            _provider.GetRequiredService<IRootFolderRelocationService>());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relocation.BeforeMetadataOnlyAtomicCommitForTest = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var changing = Task.Run(() => relocation.StartAsync(root.Id, new RootFolderPathChangeCommand(
            newRoot, RootFolderRelocationMode.MetadataOnly, false, "Moved Root", false,
            root.CaseSensitivityMode)));
        using var importScope = _provider.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = book.Id }]
        };
        using var cancellation = new CancellationTokenSource();
        Task<Microsoft.AspNetCore.Mvc.ActionResult<object>>? importing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            importing = controller.Start(request, cancellation.Token);
            Assert.False(importing.IsCompleted);
            Assert.Equal("import audio", await File.ReadAllTextAsync(incoming));
            await using var waiting = await factory.CreateDbContextAsync();
            Assert.Empty(await waiting.FileMutationJournals.ToListAsync());
            if (cancelImport)
            {
                cancellation.Cancel();
                try { await importing; } catch (OperationCanceledException) { }
                Assert.Equal("import audio", await File.ReadAllTextAsync(incoming));
            }
        }
        finally
        {
            release.TrySetResult();
            try { await changing; }
            finally
            {
                relocation.BeforeMetadataOnlyAtomicCommitForTest = null;
                if (importing != null)
                {
                    try { await importing; } catch (OperationCanceledException) { }
                }
            }
        }
        Assert.Equal(RootFolderRelocationStatus.Completed, (await changing).Status);
        if (cancelImport)
        {
            importing = controller.Start(request);
        }
        var response = await importing!;
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result);
        var destination = Path.Join(sameRoot ? newRoot : otherRoot, "Imported Book.mp3");
        Assert.Equal("import audio", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(incoming));
        Assert.False(File.Exists(Path.Join(oldRoot, "Imported Book.mp3")));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(newRoot, (await verification.RootFolders.SingleAsync(item => item.Id == root.Id)).Path);
        var owner = await verification.AudiobookFiles.SingleAsync();
        Assert.Equal(book.Id, owner.AudiobookId);
        Assert.Equal(destination, owner.Path);
        Assert.Equal(sameRoot ? newRoot : otherRoot,
            (await verification.Audiobooks.SingleAsync()).BasePath);
        var journal = await verification.FileMutationJournals.SingleAsync();
        Assert.Equal(destination, journal.DestinationPath);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Empty(await verification.MoveJobs.ToListAsync());
        AssertNoListenarrArtifacts(oldRoot);
        AssertNoListenarrArtifacts(newRoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Start_WhilePhysicalRelocationQueues_BlocksAffectedBookUntilChildCompletes(
        bool cancelImport, bool sameRoot)
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "physical-relocation-import.db")};Pooling=False")
            .Options;
        var factory = new RelocationImportSqliteFactory(options);
        Init(builder => builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext()),
                ServiceDescriptor.Singleton<IRootFolderRelocationService>(provider =>
                    ActivatorUtilities.CreateInstance<RootFolderRelocationService>(provider))));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        var oldRoot = FileService.GetTempDirectory("physical-import-old");
        var newRoot = Path.Join(FileService.GetTempPath(), "physical-import-new");
        var otherRoot = FileService.GetTempDirectory("physical-import-other");
        var sourceRoot = FileService.GetTempDirectory("physical-import-source");
        var bookFolder = Path.Join(oldRoot, "Moving Book");
        Directory.CreateDirectory(bookFolder);
        var existing = await FileService.GetFileAsync(bookFolder, "existing.mp3", "moving audio");
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "import audio");
        var root = await AddAuthorizedRootAsync(oldRoot);
        await AddAuthorizedRootAsync(otherRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = oldRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var movingBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Moving Book",
            BasePath = bookFolder,
            Authors = ["Author"]
        });
        Assert.True(await _provider.GetRequiredService<IAudiobookFileService>()
            .EnsureAudiobookFileAsync(movingBook, existing));
        var importBook = sameRoot ? movingBook : await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Imported Book",
            BasePath = otherRoot,
            Authors = ["Author"]
        });
        var relocation = Assert.IsType<RootFolderRelocationService>(
            _provider.GetRequiredService<IRootFolderRelocationService>());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relocation.BeforeTargetReservationPlanForTest = _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var changing = Task.Run(() => relocation.StartAsync(root.Id, new RootFolderPathChangeCommand(
            newRoot, RootFolderRelocationMode.Relocate, false, "Moved Root", false, root.CaseSensitivityMode)));
        using var importScope = _provider.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = importBook.Id }]
        };
        using var cancellation = new CancellationTokenSource();
        Task<Microsoft.AspNetCore.Mvc.ActionResult<object>>? importing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            importing = controller.Start(request, cancellation.Token);
            Assert.False(importing.IsCompleted);
            Assert.Equal("import audio", await File.ReadAllTextAsync(incoming));
            Assert.Equal("moving audio", await File.ReadAllTextAsync(existing));
            if (cancelImport)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
                Assert.Equal("import audio", await File.ReadAllTextAsync(incoming));
            }
        }
        finally
        {
            release.TrySetResult();
            try { await changing; }
            finally
            {
                relocation.BeforeTargetReservationPlanForTest = null;
                if (importing != null)
                {
                    try { await importing; } catch (OperationCanceledException) when (cancelImport) { }
                }
            }
        }
        Assert.NotNull((await changing).RelocationId);
        if (cancelImport) importing = controller.Start(request);
        var response = (await importing!).Result;
        if (sameRoot)
        {
            Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(response);
            Assert.Equal("import audio", await File.ReadAllTextAsync(incoming));
            await using var blocked = await factory.CreateDbContextAsync();
            Assert.Empty(await blocked.FileMutationJournals.ToListAsync());
            Assert.Single(await blocked.AudiobookFiles.ToListAsync());
        }
        else
        {
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response);
            Assert.Equal("import audio", await File.ReadAllTextAsync(Path.Join(otherRoot, "Imported Book.mp3")));
        }
        var queue = _provider.GetRequiredService<IMoveQueueService>();
        var queued = Assert.Single(await queue.GetActiveJobsAsync());
        Assert.Equal(movingBook.Id, queued.AudiobookId);
        var generation = await queue.TryClaimJobAsync(queued.Id, "physical-import-test");
        Assert.NotNull(generation);
        var claimed = Assert.IsType<MoveJob>(await queue.GetJobAsync(queued.Id));
        await _provider.GetRequiredService<IMoveJobProcessor>().ProcessJobAsync(claimed, CancellationToken.None);
        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(queued.Id));
        Assert.True(completed.Status == MoveJobStatus.Completed, completed.Error);
        await using (var notified = await factory.CreateDbContextAsync())
        {
            Assert.Equal(RootFolderRelocationStatus.Completed,
                (await notified.RootFolderRelocations.SingleAsync()).Status);
        }
        await relocation.OnMoveJobStateChangedAsync(completed.Id);
        var newBookFolder = Path.Join(newRoot, "Moving Book");
        Assert.Equal("moving audio", await File.ReadAllTextAsync(Path.Join(newBookFolder, "existing.mp3")));
        if (sameRoot)
        {
            using var retryScope = _provider.CreateScope();
            var retry = ActivatorUtilities.CreateInstance<ManualImportController>(retryScope.ServiceProvider);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await retry.Start(request)).Result);
            Assert.Equal("import audio", await File.ReadAllTextAsync(Path.Join(newBookFolder, "Moving Book.mp3")));
        }
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(newRoot, (await verification.RootFolders.SingleAsync(item => item.Id == root.Id)).Path);
        Assert.Equal(newBookFolder, (await verification.Audiobooks.SingleAsync(item => item.Id == movingBook.Id)).BasePath);
        var saga = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, saga.Status);
        Assert.Null(saga.ActiveRootFolderId);
        Assert.Equal(2, await verification.AudiobookFiles.CountAsync());
        Assert.Equal(FileMutationJournalState.Completed, (await verification.FileMutationJournals.SingleAsync()).State);
        Assert.False(File.Exists(incoming));
        AssertNoListenarrArtifacts(newRoot);
        AssertNoListenarrArtifacts(otherRoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Start_WhileImportRegisters_RelocationUsesCommittedManifest(bool cancelRelocation, bool sameRoot)
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "import-relocation.db")};Pooling=False")
            .Options;
        var factory = new RelocationImportSqliteFactory(options);
        Init(builder => builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext()),
                ServiceDescriptor.Singleton<IRootFolderRelocationService>(provider =>
                    ActivatorUtilities.CreateInstance<RootFolderRelocationService>(provider))));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        var oldRoot = FileService.GetTempDirectory("import-physical-old");
        var newRoot = FileService.GetTempDirectory("import-physical-new");
        var otherRoot = FileService.GetTempDirectory("import-physical-other");
        var sourceRoot = FileService.GetTempDirectory("import-physical-source");
        var bookFolder = Path.Join(oldRoot, "Moving Book");
        Directory.CreateDirectory(bookFolder);
        var existing = await FileService.GetFileAsync(bookFolder, "existing.mp3", "moving audio");
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "import audio");
        var root = await AddAuthorizedRootAsync(oldRoot);
        await AddAuthorizedRootAsync(otherRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = oldRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var movingBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Moving Book",
            BasePath = bookFolder,
            Authors = ["Author"]
        });
        Assert.True(await _provider.GetRequiredService<IAudiobookFileService>()
            .EnsureAudiobookFileAsync(movingBook, existing));
        var importBook = sameRoot ? movingBook : await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Imported Book",
            BasePath = otherRoot,
            Authors = ["Author"]
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadata.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return new AudioMetadata { Format = "mp3", Duration = TimeSpan.FromSeconds(1) };
            });
        using var importScope = _provider.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var importing = controller.Start(new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = importBook.Id }]
        });
        var relocation = _provider.GetRequiredService<IRootFolderRelocationService>();
        var command = new RootFolderPathChangeCommand(newRoot, RootFolderRelocationMode.Relocate,
            false, "Moved Root", false, root.CaseSensitivityMode);
        using var cancellation = new CancellationTokenSource();
        Task<RootFolderPathChangeResult>? changing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            changing = relocation.StartAsync(root.Id, command, cancellation.Token);
            Assert.False(changing.IsCompleted);
            await using var paused = await factory.CreateDbContextAsync();
            Assert.Empty(await paused.RootFolderRelocations.ToListAsync());
            Assert.Empty(await paused.MoveJobs.ToListAsync());
            Assert.Equal(oldRoot, (await paused.RootFolders.SingleAsync(item => item.Id == root.Id)).Path);
            Assert.Equal("moving audio", await File.ReadAllTextAsync(existing));
            if (cancelRelocation)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changing);
            }
        }
        finally
        {
            release.TrySetResult();
            try { await importing; }
            finally
            {
                if (changing != null)
                {
                    try { await changing; } catch (OperationCanceledException) when (cancelRelocation) { }
                }
            }
        }
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await importing).Result);
        Assert.False(File.Exists(incoming));
        if (cancelRelocation) changing = relocation.StartAsync(root.Id, command);
        Assert.NotNull((await changing!).RelocationId);
        var queue = _provider.GetRequiredService<IMoveQueueService>();
        var queued = Assert.Single(await queue.GetActiveJobsAsync());
        Assert.NotNull(await queue.TryClaimJobAsync(queued.Id, "import-physical-test"));
        var claimed = Assert.IsType<MoveJob>(await queue.GetJobAsync(queued.Id));
        await _provider.GetRequiredService<IMoveJobProcessor>().ProcessJobAsync(claimed, CancellationToken.None);
        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(queued.Id));
        Assert.True(completed.Status == MoveJobStatus.Completed, completed.Error);
        var newBookFolder = Path.Join(newRoot, "Moving Book");
        var importedDestination = sameRoot ? Path.Join(newBookFolder, "Moving Book.mp3")
            : Path.Join(otherRoot, "Imported Book.mp3");
        Assert.Equal("moving audio", await File.ReadAllTextAsync(Path.Join(newBookFolder, "existing.mp3")));
        Assert.Equal("import audio", await File.ReadAllTextAsync(importedDestination));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(newRoot, (await verification.RootFolders.SingleAsync(item => item.Id == root.Id)).Path);
        Assert.Equal(newBookFolder, (await verification.Audiobooks.SingleAsync(item => item.Id == movingBook.Id)).BasePath);
        var owners = await verification.AudiobookFiles.ToListAsync();
        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, owner => owner.AudiobookId == movingBook.Id
            && owner.Path == Path.Join(newBookFolder, "existing.mp3"));
        Assert.Contains(owners, owner => owner.AudiobookId == importBook.Id && owner.Path == importedDestination);
        var saga = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, saga.Status);
        Assert.Null(saga.ActiveRootFolderId);
        Assert.Equal(FileMutationJournalState.Completed, (await verification.FileMutationJournals.SingleAsync()).State);
        AssertNoListenarrArtifacts(newRoot);
        AssertNoListenarrArtifacts(otherRoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true, 1)]
    [InlineData(true, false, true, 1)]
    [InlineData(false, true, true, 1)]
    [InlineData(true, true, true, 1)]
    [InlineData(false, false, false, 1)]
    [InlineData(true, false, false, 1)]
    [InlineData(false, true, false, 1)]
    [InlineData(true, true, false, 1)]
    [InlineData(false, false, true, 2)]
    [InlineData(true, false, true, 2)]
    [InlineData(false, true, true, 2)]
    [InlineData(true, true, true, 2)]
    [InlineData(false, false, false, 2)]
    [InlineData(true, false, false, 2)]
    [InlineData(false, true, false, 2)]
    [InlineData(true, true, false, 2)]
    [InlineData(false, false, true, 2, true)]
    [InlineData(true, false, true, 2, true)]
    [InlineData(false, true, true, 2, true)]
    [InlineData(true, true, true, 2, true)]
    [InlineData(false, false, true, 2, true, 1)]
    [InlineData(true, false, true, 2, true, 1)]
    [InlineData(false, true, true, 2, true, 1)]
    [InlineData(true, true, true, 2, true, 1)]
    [InlineData(false, false, true, 2, true, 2)]
    [InlineData(true, false, true, 2, true, 2)]
    [InlineData(false, true, true, 2, true, 2)]
    [InlineData(true, true, true, 2, true, 2)]
    public async Task Start_WhileRecoveryCommits_RetainsOldSourceAndPublishesNewImport(
        bool cancelImport, bool sameAudiobook, bool recoveryFirst = true, int sourceReplacement = 0,
        bool failRecovery = false, int recoveryFailureStage = 0)
    {
        var pause = new PauseRecoveryCommitInterceptor
        {
            FailAfterRelease = failRecovery,
            FailureStage = recoveryFailureStage
        };
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "recovery-import.db")};Pooling=False")
            .AddInterceptors(pause).Options;
        var factory = new RelocationImportSqliteFactory(options);
        Init(builder => builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext())));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        var outputRoot = FileService.GetTempDirectory("recovery-import-out");
        var sourceRoot = FileService.GetTempDirectory("recovery-import-source");
        var retainedRoot = FileService.GetTempDirectory("recovery-import-retained");
        await AddAuthorizedRootAsync(outputRoot);
        var oldSource = await FileService.GetFileAsync(retainedRoot, "old.mp3", "recovered audio");
        var oldDestination = Path.Join(outputRoot, "recovered.mp3");
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "new audio");
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var recoveredBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Recovered Book",
            BasePath = outputRoot,
            Authors = ["Author"]
        });
        var importBook = sameAudiobook ? recoveredBook : await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "New Book",
            BasePath = outputRoot,
            Authors = ["Author"]
        });
        var operationId = Guid.NewGuid();
        using (var lease = await _provider.GetRequiredService<IFileMover>()
            .PrepareActionForRegistrationAsync(FileAction.Move, oldSource, oldDestination, operationId))
        {
            Assert.NotNull(lease);
            await using var ownerDb = await factory.CreateDbContextAsync();
            var owner = AudiobookFile.CreateUnresolved(oldDestination);
            owner.AudiobookId = recoveredBook.Id;
            owner.ApplyPathIdentity(oldDestination, AudiobookFilePathIdentity.CreateValid(
                oldDestination, FileSystemPathSemantics.CurrentHostDefault,
                FileSystemCaseSensitivityMode.Auto, outputRoot));
            ownerDb.AudiobookFiles.Add(owner);
            await ownerDb.SaveChangesAsync();
        }
        var retainedSourceBytes = sourceReplacement == 2 ? "foreign source" : "recovered audio";
        if (sourceReplacement != 0)
        {
            File.Move(oldSource, oldSource + ".displaced");
            await File.WriteAllTextAsync(oldSource, retainedSourceBytes);
        }
        using var recoveryScope = _provider.CreateScope();
        var recovery = ActivatorUtilities.CreateInstance<FileRegistrationRecoveryController>(recoveryScope.ServiceProvider);
        Task<Microsoft.AspNetCore.Mvc.IActionResult>? recovering = null;
        var importEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!recoveryFirst)
        {
            _metadata.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .Returns(async () =>
                {
                    importEntered.TrySetResult();
                    await importRelease.Task;
                    return new AudioMetadata { Format = "mp3", Duration = TimeSpan.FromSeconds(1) };
                });
        }
        using var importScope = _provider.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = importBook.Id }]
        };
        using var cancellation = new CancellationTokenSource();
        Task<Microsoft.AspNetCore.Mvc.ActionResult<object>>? importing = null;
        try
        {
            if (recoveryFirst)
            {
                pause.Arm();
                recovering = recovery.Retry(operationId, CancellationToken.None);
                await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                importing = controller.Start(request, cancellation.Token);
                Assert.False(importing.IsCompleted);
            }
            else
            {
                importing = controller.Start(request);
                await importEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                recovering = recovery.Retry(operationId, cancellation.Token);
                Assert.False(recovering.IsCompleted);
            }
            using (var reader = new StreamReader(new FileStream(incoming, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            {
                Assert.Equal("new audio", await reader.ReadToEndAsync());
            }
            Assert.Equal(retainedSourceBytes, await File.ReadAllTextAsync(oldSource));
            await using var waiting = await factory.CreateDbContextAsync();
            Assert.Equal(recoveryFirst ? 1 : 2, await waiting.FileMutationJournals.CountAsync());
            Assert.Single(await waiting.AudiobookFiles.ToListAsync());
            if (recoveryFirst)
            {
                var journal = await waiting.FileMutationJournals.SingleAsync();
                if (recoveryFailureStage == 0)
                {
                    Assert.Null(journal.AudiobookId);
                }
                else
                {
                    Assert.Equal(1, pause.AffectedRows);
                    Assert.Equal(recoveredBook.Id, journal.AudiobookId);
                    Assert.Equal(recoveryFailureStage == 1 ? FileMutationJournalState.TargetVerified
                        : FileMutationJournalState.CompletedSourceRetained, journal.State);
                }
            }
            else
            {
                Assert.Equal(sameAudiobook ? FileMutationJournalState.CompletedSourceRetained
                    : FileMutationJournalState.TargetVerified,
                    (await waiting.FileMutationJournals.SingleAsync(journal => journal.OperationId == operationId)).State);
            }
            if (cancelImport)
            {
                cancellation.Cancel();
                if (recoveryFirst)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
                }
                else
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovering);
                }
                using var reader = new StreamReader(new FileStream(incoming, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                Assert.Equal("new audio", await reader.ReadToEndAsync());
            }
        }
        finally
        {
            pause.Release.TrySetResult();
            importRelease.TrySetResult();
            try
            {
                if (recovering != null)
                {
                    try { await recovering; }
                    catch (OperationCanceledException) when (cancelImport && !recoveryFirst) { }
                    catch (IOException) when (failRecovery) { }
                }
            }
            finally
            {
                if (importing != null)
                {
                    try { await importing; } catch (OperationCanceledException) when (cancelImport) { }
                }
            }
        }
        if (failRecovery)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => recovering!);
            Assert.Equal("Injected recovery journal update failure.", error.Message);
            recovering = recovery.Retry(operationId, CancellationToken.None);
        }
        if (cancelImport && !recoveryFirst) recovering = recovery.Retry(operationId, CancellationToken.None);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await recovering!);
        if (cancelImport && recoveryFirst) importing = controller.Start(request);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await importing!).Result);
        var destination = Path.Join(outputRoot, $"{importBook.Title}.mp3");
        Assert.Equal("new audio", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(incoming));
        Assert.Equal(retainedSourceBytes, await File.ReadAllTextAsync(oldSource));
        Assert.Equal("recovered audio", await File.ReadAllTextAsync(oldDestination));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await recovery.Retry(operationId, CancellationToken.None));
            Assert.Equal(retainedSourceBytes, await File.ReadAllTextAsync(oldSource));
            Assert.Equal("recovered audio", await File.ReadAllTextAsync(oldDestination));
            if (sourceReplacement != 0)
            {
                Assert.Equal("recovered audio", await File.ReadAllTextAsync(oldSource + ".displaced"));
            }
        }
        await using var verification = await factory.CreateDbContextAsync();
        var journals = await verification.FileMutationJournals.AsNoTracking().ToListAsync();
        Assert.Equal(2, journals.Count);
        Assert.Equal(FileMutationJournalState.CompletedSourceRetained,
            journals.Single(journal => journal.OperationId == operationId).State);
        Assert.Equal(FileMutationJournalState.Completed,
            journals.Single(journal => journal.OperationId != operationId).State);
        var owners = await verification.AudiobookFiles.AsNoTracking().ToListAsync();
        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, owner => owner.AudiobookId == recoveredBook.Id && owner.Path == oldDestination);
        Assert.Contains(owners, owner => owner.AudiobookId == importBook.Id && owner.Path == destination);
        AssertNoListenarrArtifacts(outputRoot);
        AssertNoListenarrArtifacts(retainedRoot);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public async Task Start_WhileRecoveryNeedsAttention_PreservesReplacementsAndIsolatesUnrelatedRoot(
        bool cancelImport, bool sameAudiobook, bool replaceSource, bool failAcknowledgement = false)
    {
        var pause = new PauseRecoveryCommitInterceptor
        {
            FailureStage = failAcknowledgement ? 2 : 0,
            FailAfterRelease = failAcknowledgement
        };
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "attention-import.db")};Pooling=False")
            .AddInterceptors(pause).Options;
        var factory = new RelocationImportSqliteFactory(options);
        Init(builder => builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext())));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        var outputRoot = FileService.GetTempDirectory("attention-import-out");
        var otherRoot = FileService.GetTempDirectory("attention-import-other");
        var sourceRoot = FileService.GetTempDirectory("attention-import-source");
        var retainedRoot = FileService.GetTempDirectory("attention-import-retained");
        await AddAuthorizedRootAsync(outputRoot);
        await AddAuthorizedRootAsync(otherRoot);
        var oldSource = await FileService.GetFileAsync(retainedRoot, "old.mp3", "recovered audio");
        var oldDestination = Path.Join(outputRoot, "recovered.mp3");
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "new audio");
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var recoveredBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Recovered Book",
            BasePath = outputRoot,
            Authors = ["Author"]
        });
        var importBook = sameAudiobook ? recoveredBook : await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "New Book",
            BasePath = otherRoot,
            Authors = ["Author"]
        });
        var operationId = Guid.NewGuid();
        using (var lease = await _provider.GetRequiredService<IFileMover>()
            .PrepareActionForRegistrationAsync(FileAction.Move, oldSource, oldDestination, operationId))
        {
            Assert.NotNull(lease);
            await using var ownerDb = await factory.CreateDbContextAsync();
            var owner = AudiobookFile.CreateUnresolved(oldDestination);
            owner.AudiobookId = recoveredBook.Id;
            owner.ApplyPathIdentity(oldDestination, AudiobookFilePathIdentity.CreateValid(
                oldDestination, FileSystemPathSemantics.CurrentHostDefault,
                FileSystemCaseSensitivityMode.Auto, outputRoot));
            ownerDb.AudiobookFiles.Add(owner);
            await ownerDb.SaveChangesAsync();
        }
        await _provider.GetRequiredService<IFileRegistrationRecoveryService>().AdoptCommittedAnonymousAsync();
        File.Move(oldDestination, oldDestination + ".displaced");
        await File.WriteAllTextAsync(oldDestination, "foreign target");
        if (replaceSource)
        {
            File.Move(oldSource, oldSource + ".displaced");
            await File.WriteAllTextAsync(oldSource, "foreign source");
        }
        using var recoveryScope = _provider.CreateScope();
        var recovery = ActivatorUtilities.CreateInstance<FileRegistrationRecoveryController>(recoveryScope.ServiceProvider);
        pause.Arm();
        var recovering = recovery.Retry(operationId, CancellationToken.None);
        using var importScope = _provider.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = importBook.Id }]
        };
        using var cancellation = new CancellationTokenSource();
        Task<Microsoft.AspNetCore.Mvc.ActionResult<object>>? importing = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            importing = controller.Start(request, cancellation.Token);
            Assert.False(importing.IsCompleted);
            Assert.Equal("new audio", await File.ReadAllTextAsync(incoming));
            await using var paused = await factory.CreateDbContextAsync();
            var journal = await paused.FileMutationJournals.SingleAsync();
            Assert.Equal(failAcknowledgement ? FileMutationJournalState.NeedsAttention
                : FileMutationJournalState.TargetVerified, journal.State);
            if (failAcknowledgement) Assert.Equal(1, pause.AffectedRows);
            Assert.Equal(recoveredBook.Id, journal.AudiobookId);
            Assert.Single(await paused.AudiobookFiles.ToListAsync());
            if (cancelImport)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
            }
        }
        finally
        {
            pause.Release.TrySetResult();
            try
            {
                try { await recovering; }
                catch (IOException) when (failAcknowledgement) { }
            }
            finally
            {
                if (importing != null)
                {
                    try { await importing; } catch (OperationCanceledException) when (cancelImport) { }
                }
            }
        }
        if (failAcknowledgement)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => recovering);
            Assert.Equal("Injected recovery journal update failure.", error.Message);
            recovering = recovery.Retry(operationId, CancellationToken.None);
        }
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await recovering);
        if (cancelImport) importing = controller.Start(request);
        var result = (await importing!).Result;
        if (sameAudiobook)
        {
            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
            Assert.Contains("registration_recovery_repair_required", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
            Assert.Equal("new audio", await File.ReadAllTextAsync(incoming));
            Assert.False(File.Exists(Path.Join(outputRoot, "Recovered Book.mp3")));
        }
        else
        {
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.False(File.Exists(incoming));
            Assert.Equal("new audio", await File.ReadAllTextAsync(Path.Join(otherRoot, "New Book.mp3")));
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await recovery.Retry(operationId, CancellationToken.None));
            Assert.Equal("foreign target", await File.ReadAllTextAsync(oldDestination));
            Assert.Equal("recovered audio", await File.ReadAllTextAsync(oldDestination + ".displaced"));
            Assert.Equal(replaceSource ? "foreign source" : "recovered audio", await File.ReadAllTextAsync(oldSource));
            if (replaceSource) Assert.Equal("recovered audio", await File.ReadAllTextAsync(oldSource + ".displaced"));
        }
        File.Move(oldDestination, oldDestination + ".foreign-retained");
        File.Copy(oldDestination + ".displaced", oldDestination);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await recovery.Retry(operationId, CancellationToken.None));
        var blockedIncoming = await FileService.GetFileAsync(sourceRoot, "blocked.mp3", "blocked audio");
        using (var retryScope = _provider.CreateScope())
        {
            var retry = ActivatorUtilities.CreateInstance<ManualImportController>(retryScope.ServiceProvider);
            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>((await retry.Start(
                new ManualImportRequestDto
                {
                    Path = sourceRoot,
                    Mode = "interactive",
                    Action = FileAction.Move,
                    Items = [new ManualImportItemDto { FullPath = blockedIncoming, MatchedAudiobookId = recoveredBook.Id }]
                })).Result);
            Assert.Contains("registration_recovery_repair_required", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        }
        Assert.Equal("blocked audio", await File.ReadAllTextAsync(blockedIncoming));
        Assert.Equal("recovered audio", await File.ReadAllTextAsync(oldDestination));
        Assert.Equal("foreign target", await File.ReadAllTextAsync(oldDestination + ".foreign-retained"));
        Assert.Equal(replaceSource ? "foreign source" : "recovered audio", await File.ReadAllTextAsync(oldSource));
        await using var verification = await factory.CreateDbContextAsync();
        var journals = await verification.FileMutationJournals.AsNoTracking().ToListAsync();
        Assert.Equal(sameAudiobook ? 1 : 2, journals.Count);
        Assert.Equal(FileMutationJournalState.NeedsAttention,
            journals.Single(journal => journal.OperationId == operationId).State);
        var owners = await verification.AudiobookFiles.AsNoTracking().ToListAsync();
        Assert.Equal(sameAudiobook ? 1 : 2, owners.Count);
        Assert.Contains(owners, owner => owner.AudiobookId == recoveredBook.Id && owner.Path == oldDestination);
        if (!sameAudiobook)
        {
            Assert.Contains(owners, owner => owner.AudiobookId == importBook.Id && owner.Path == Path.Join(otherRoot, "New Book.mp3"));
            Assert.Equal(FileMutationJournalState.Completed,
                journals.Single(journal => journal.OperationId != operationId).State);
        }
        AssertNoListenarrArtifacts(outputRoot);
        AssertNoListenarrArtifacts(otherRoot);
        AssertNoListenarrArtifacts(retainedRoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    public async Task Start_OwnerCommitFails_RecoveryWaitsAndUsesPersistedOwnership(
        bool afterCommit, bool cancelRecovery, bool failPublication = false, bool sameRequestBatch = false)
    {
        var outputRoot = FileService.GetTempDirectory("failed-import-out");
        var sourceRoot = FileService.GetTempDirectory("failed-import-source");
        var destination = Path.Join(outputRoot, "Failed Import.mp3");
        var pause = new PauseImportOwnerCommitInterceptor(destination, afterCommit);
        var importErrors = new List<Exception>();
        var importLogger = new Mock<ILogger<ManualImportController>>();
        var fileLogger = new Mock<ILogger<AudiobookFileService>>();
        fileLogger.Setup(logger => logger.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                if (invocation.Arguments[3] is Exception error) importErrors.Add(error);
            }));
        importLogger.Setup(logger => logger.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                if (invocation.Arguments[3] is Exception error) importErrors.Add(error);
            }));
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "failed-import.db")};Pooling=False")
            .AddInterceptors(pause).Options;
        var factory = new RelocationImportSqliteFactory(options);
        Init(builder =>
        {
            builder.WithSingleton(importLogger.Object);
            builder.WithSingleton(fileLogger.Object);
            builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
                .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext()));
            if (failPublication)
            {
                builder.WithSingleton<IFileMover>(new FileMover(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<FileMover>.Instance,
                    dbContextFactory: factory, timeProvider: TimeProvider.System)
                {
                    FileMoveLockDirectoryForTest = FileService.GetTempDirectory("failed-publication-locks"),
                    AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync = pause.FailPublicationAsync
                });
            }
        });
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        await AddAuthorizedRootAsync(outputRoot);
        var incoming = await FileService.GetFileAsync(sourceRoot, "incoming.mp3", "failed import audio");
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var book = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Failed Import",
            BasePath = outputRoot,
            Authors = ["Author"]
        });
        var otherRoot = FileService.GetTempDirectory("failed-import-other");
        await AddAuthorizedRootAsync(otherRoot);
        var otherIncoming = await FileService.GetFileAsync(sourceRoot, "other.mp3", "other audio");
        var otherBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Other Book",
            BasePath = otherRoot,
            Authors = ["Author"]
        });
        using var importScope = _provider.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var importing = controller.Start(new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = sameRequestBatch
                ? [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = otherIncoming, MatchedAudiobookId = otherBook.Id }]
                : [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = book.Id }]
        });
        using var recoveryScope = _provider.CreateScope();
        var recovery = ActivatorUtilities.CreateInstance<FileRegistrationRecoveryController>(recoveryScope.ServiceProvider);
        using var cancellation = new CancellationTokenSource();
        Guid operationId = Guid.Empty;
        Task<Microsoft.AspNetCore.Mvc.IActionResult>? recovering = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var paused = await factory.CreateDbContextAsync();
            var journal = await paused.FileMutationJournals.SingleAsync();
            operationId = journal.OperationId;
            Assert.Equal(failPublication ? FileMutationJournalState.TargetIdentityPersisted
                : FileMutationJournalState.TargetVerified, journal.State);
            Assert.Null(journal.AudiobookId);
            Assert.Equal(afterCommit ? 1 : 0, await paused.AudiobookFiles.CountAsync());
            recovering = recovery.Retry(operationId, cancellation.Token);
            Assert.False(recovering.IsCompleted);
            using var reader = new StreamReader(new FileStream(incoming, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            Assert.Equal("failed import audio", await reader.ReadToEndAsync());
            if (cancelRecovery)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovering);
            }
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await importing; }
            finally
            {
                if (recovering != null)
                {
                    try { await recovering; } catch (OperationCanceledException) when (cancelRecovery) { }
                }
            }
        }
        var importResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await importing).Result);
        var importedCount = Assert.IsType<int>(importResult.Value!.GetType().GetProperty("importedCount")!.GetValue(importResult.Value));
        Assert.True(importedCount == (sameRequestBatch ? 1 : 0),
            System.Text.Json.JsonSerializer.Serialize(importResult.Value) + Environment.NewLine
            + string.Join(Environment.NewLine, importErrors));
        Assert.Equal(1, pause.Hits);
        if (cancelRecovery) recovering = recovery.Retry(operationId, CancellationToken.None);
        if (afterCommit) Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await recovering!);
        else Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await recovering!);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var retry = await recovery.Retry(operationId, CancellationToken.None);
            if (afterCommit) Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(retry);
            else Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(retry);
            Assert.Equal("failed import audio", await File.ReadAllTextAsync(incoming));
            Assert.Equal("failed import audio", await File.ReadAllTextAsync(destination));
        }
        await using (var verification = await factory.CreateDbContextAsync())
        {
            var journal = await verification.FileMutationJournals.SingleAsync(item => item.OperationId == operationId);
            Assert.Equal(afterCommit ? FileMutationJournalState.CompletedSourceRetained
                : FileMutationJournalState.NeedsAttention, journal.State);
            Assert.Equal((afterCommit ? 1 : 0) + (sameRequestBatch ? 1 : 0), await verification.AudiobookFiles.CountAsync());
            if (afterCommit)
            {
                var owner = await verification.AudiobookFiles.SingleAsync(file => file.AudiobookId == book.Id);
                Assert.Equal(book.Id, owner.AudiobookId);
                Assert.Equal(destination, owner.Path);
            }
        }
        if (!sameRequestBatch)
        {
            using var nextScope = _provider.CreateScope();
            var next = ActivatorUtilities.CreateInstance<ManualImportController>(nextScope.ServiceProvider);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await next.Start(new ManualImportRequestDto
            {
                Path = sourceRoot,
                Mode = "interactive",
                Action = FileAction.Move,
                Items = [new ManualImportItemDto { FullPath = otherIncoming, MatchedAudiobookId = otherBook.Id }]
            })).Result);
        }
        Assert.Equal("other audio", await File.ReadAllTextAsync(Path.Join(otherRoot, "Other Book.mp3")));
        Assert.False(File.Exists(otherIncoming));
        Assert.Equal("failed import audio", await File.ReadAllTextAsync(incoming));
        Assert.Equal("failed import audio", await File.ReadAllTextAsync(destination));
        await using var final = await factory.CreateDbContextAsync();
        Assert.Equal(afterCommit ? 2 : 1, await final.AudiobookFiles.CountAsync());
        var unrelatedOwner = await final.AudiobookFiles.SingleAsync(file => file.AudiobookId == otherBook.Id);
        Assert.Equal(Path.Join(otherRoot, "Other Book.mp3"), unrelatedOwner.Path);
        Assert.Equal(2, await final.FileMutationJournals.CountAsync());
        Assert.Equal(FileMutationJournalState.Completed,
            (await final.FileMutationJournals.SingleAsync(journal => journal.OperationId != operationId)).State);
        AssertNoListenarrArtifacts(outputRoot);
        AssertNoListenarrArtifacts(otherRoot);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Start_CanceledDuringSecondOwnerCommit_PreservesPartialBatchAndRetriesUnstartedItem(
        bool afterCommit, bool failCommit, bool sameAudiobook)
    {
        var outputRoot = FileService.GetTempDirectory("partial-batch-out");
        var sourceRoot = FileService.GetTempDirectory("partial-batch-source");
        var firstDestination = Path.Join(outputRoot, sameAudiobook ? "Batch - 1.mp3" : "First.mp3");
        var secondDestination = Path.Join(outputRoot, sameAudiobook ? "Batch - 2.mp3" : "Second.mp3");
        var pause = new PauseImportOwnerCommitInterceptor(secondDestination, afterCommit, failCommit, ignoreCancellation: true);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "partial-batch.db")};Pooling=False")
            .AddInterceptors(pause).Options;
        var factory = new RelocationImportSqliteFactory(options);
        Init(builder => builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext())));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        await AddAuthorizedRootAsync(outputRoot);
        var otherRoot = FileService.GetTempDirectory("partial-batch-other");
        await AddAuthorizedRootAsync(otherRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.MultiFileNamingPattern = "{Title} - {ChapterNumber}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var firstBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = sameAudiobook ? "Batch" : "First",
            BasePath = outputRoot,
            Authors = ["Author"]
        });
        var secondBook = sameAudiobook ? firstBook : await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Second",
            BasePath = outputRoot,
            Authors = ["Author"]
        });
        var thirdBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Third",
            BasePath = otherRoot,
            Authors = ["Author"]
        });
        var firstSource = await FileService.GetFileAsync(sourceRoot, "first.mp3", "first audio");
        var secondSource = await FileService.GetFileAsync(sourceRoot, "second.mp3", "second audio");
        var thirdSource = await FileService.GetFileAsync(sourceRoot, "third.mp3", "third audio");
        using var importScope = _provider.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var importing = controller.Start(new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = firstSource, MatchedAudiobookId = firstBook.Id },
                new ManualImportItemDto { FullPath = secondSource, MatchedAudiobookId = secondBook.Id },
                new ManualImportItemDto { FullPath = thirdSource, MatchedAudiobookId = thirdBook.Id }]
        }, cancellation.Token);
        using var recoveryScope = _provider.CreateScope();
        var recovery = ActivatorUtilities.CreateInstance<FileRegistrationRecoveryController>(recoveryScope.ServiceProvider);
        Guid operationId = Guid.Empty;
        Task<Microsoft.AspNetCore.Mvc.IActionResult>? recovering = null;
        try
        {
            var reached = await Task.WhenAny(pause.Entered.Task, importing)
                .WaitAsync(TimeSpan.FromSeconds(60));
            if (reached == importing)
            {
                var earlyResult = await importing;
                Assert.Fail("Import completed before the second owner commit pause: "
                    + System.Text.Json.JsonSerializer.Serialize(earlyResult.Result));
            }
            await using var paused = await factory.CreateDbContextAsync();
            var journals = await paused.FileMutationJournals.AsNoTracking().ToListAsync();
            Assert.Equal(2, journals.Count);
            var secondJournal = Assert.Single(journals, journal => journal.DestinationPath == secondDestination);
            operationId = secondJournal.OperationId;
            Assert.Equal(FileMutationJournalState.TargetVerified, secondJournal.State);
            Assert.Equal(afterCommit ? 2 : 1, await paused.AudiobookFiles.CountAsync());
            Assert.Equal(FileMutationJournalState.Completed,
                Assert.Single(journals, journal => journal.DestinationPath == firstDestination).State);
            Assert.False(File.Exists(firstSource));
            Assert.Equal("first audio", await File.ReadAllTextAsync(firstDestination));
            recovering = recovery.Retry(operationId, CancellationToken.None);
            Assert.False(recovering.IsCompleted);
            cancellation.Cancel();
            Assert.False(importing.IsCompleted);
            Assert.False(recovering.IsCompleted);
            Assert.Equal("third audio", await File.ReadAllTextAsync(thirdSource));
            Assert.False(File.Exists(Path.Join(otherRoot, "Third.mp3")));
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await importing; }
            finally { if (recovering != null) await recovering; }
        }
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await importing).Result);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        Assert.True(payload.GetProperty("stoppedByCancellation").GetBoolean());
        Assert.Equal(3, payload.GetProperty("totalCount").GetInt32());
        Assert.Equal(failCommit ? 1 : 2, payload.GetProperty("importedCount").GetInt32());
        Assert.Equal(2, payload.GetProperty("results").GetArrayLength());
        Assert.DoesNotContain(payload.GetProperty("results").EnumerateArray(),
            item => item.GetProperty(nameof(ManualImportResultDto.SourcePath)).GetString() == thirdSource);
        Assert.Equal(1, pause.Hits);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var retry = await recovery.Retry(operationId, CancellationToken.None);
            if (afterCommit || !failCommit) Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(retry);
            else Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(retry);
            Assert.Equal("first audio", await File.ReadAllTextAsync(firstDestination));
            Assert.Equal("second audio", await File.ReadAllTextAsync(secondDestination));
            Assert.Equal("third audio", await File.ReadAllTextAsync(thirdSource));
            Assert.Equal(failCommit, File.Exists(secondSource));
            if (File.Exists(secondSource)) Assert.Equal("second audio", await File.ReadAllTextAsync(secondSource));
        }
        await using (var verification = await factory.CreateDbContextAsync())
        {
            Assert.Equal(afterCommit || !failCommit ? 2 : 1, await verification.AudiobookFiles.CountAsync());
            Assert.False(await verification.AudiobookFiles.AnyAsync(file => file.AudiobookId == thirdBook.Id));
            var journal = await verification.FileMutationJournals.SingleAsync(item => item.OperationId == operationId);
            Assert.Equal(!failCommit ? FileMutationJournalState.Completed
                : afterCommit ? FileMutationJournalState.CompletedSourceRetained : FileMutationJournalState.NeedsAttention, journal.State);
        }
        using var nextScope = _provider.CreateScope();
        var next = ActivatorUtilities.CreateInstance<ManualImportController>(nextScope.ServiceProvider);
        var nextResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await next.Start(new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = thirdSource, MatchedAudiobookId = thirdBook.Id }]
        })).Result);
        Assert.Equal(1, System.Text.Json.JsonSerializer.SerializeToElement(nextResult.Value)
            .GetProperty("importedCount").GetInt32());
        Assert.False(File.Exists(thirdSource));
        Assert.Equal("third audio", await File.ReadAllTextAsync(Path.Join(otherRoot, "Third.mp3")));
        await using var final = await factory.CreateDbContextAsync();
        Assert.Equal(afterCommit || !failCommit ? 3 : 2, await final.AudiobookFiles.CountAsync());
        Assert.Equal(3, await final.FileMutationJournals.CountAsync());
        Assert.Equal(firstBook.Id,
            (await final.AudiobookFiles.SingleAsync(file => file.Path == firstDestination)).AudiobookId);
        if (afterCommit || !failCommit)
        {
            Assert.Equal(secondBook.Id,
                (await final.AudiobookFiles.SingleAsync(file => file.Path == secondDestination)).AudiobookId);
        }
        else
        {
            Assert.False(await final.AudiobookFiles.AnyAsync(file => file.Path == secondDestination));
        }
        Assert.Equal(Path.Join(otherRoot, "Third.mp3"),
            (await final.AudiobookFiles.SingleAsync(file => file.AudiobookId == thirdBook.Id)).Path);
        AssertNoListenarrArtifacts(outputRoot);
        AssertNoListenarrArtifacts(otherRoot);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Start_CompanionPublicationInterrupted_PreservesAudioAndRecoversWithoutSourceDeletion(
        bool beforeSourceDelete, bool failPublication, bool cancelImport)
    {
        var outputRoot = FileService.GetTempDirectory("companion-overlap-out");
        var sourceRoot = FileService.GetTempDirectory("companion-overlap-source");
        var bookFolder = Path.Join(outputRoot, "Companion Book");
        var companionDestination = Path.Join(bookFolder, "cover.jpg");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "companion-overlap.db")};Pooling=False")
            .Options;
        var factory = new RelocationImportSqliteFactory(options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hits = 0;
        var companionMessages = new List<string>();
        var companionLogger = new Mock<ILogger<ManualImportCompanionImporter>>();
        companionLogger.Setup(logger => logger.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation => companionMessages.Add(
                invocation.Arguments[2] + Environment.NewLine + invocation.Arguments[3])));
        async Task PauseCompanionAsync()
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await db.FileMutationJournals.AnyAsync(journal => journal.DestinationPath == companionDestination)
                && Interlocked.CompareExchange(ref hits, 1, 0) == 0)
            {
                entered.TrySetResult();
                await release.Task;
                if (failPublication) throw new IOException("Injected companion publication failure.");
            }
        }
        var mover = new FileMover(Microsoft.Extensions.Logging.Abstractions.NullLogger<FileMover>.Instance,
            dbContextFactory: factory, timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory("companion-overlap-locks"),
            ForceCrossVolumeForTest = true,
            AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync =
                beforeSourceDelete ? null : PauseCompanionAsync,
            BeforeMarkerlessRegistrationSourceDeleteForTestAsync =
                beforeSourceDelete ? PauseCompanionAsync : null
        };
        Init(builder => builder.WithSingleton<IFileMover>(mover)
            .WithSingleton(companionLogger.Object)
            .WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext()),
                ServiceDescriptor.Scoped<ManualImportCompanionImporter, ManualImportCompanionImporter>()));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        await AddAuthorizedRootAsync(outputRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "{Title}";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var book = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Companion Book",
            Authors = ["Author"]
        });
        var incoming = await FileService.GetFileAsync(sourceRoot, "selected.mp3", "selected audio");
        var companion = await FileService.GetFileAsync(sourceRoot, "cover.jpg", "cover bytes");
        var otherRoot = FileService.GetTempDirectory("companion-overlap-other");
        var otherSource = FileService.GetTempDirectory("companion-overlap-other-source");
        await AddAuthorizedRootAsync(otherRoot);
        var otherIncoming = await FileService.GetFileAsync(otherSource, "other.mp3", "other audio");
        var otherBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Other Book",
            BasePath = otherRoot,
            Authors = ["Author"]
        });
        using var importScope = _provider.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var importing = controller.Start(new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            IncludeCompanionFiles = true,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = book.Id }]
        }, cancellation.Token);
        using var nextScope = _provider.CreateScope();
        var next = ActivatorUtilities.CreateInstance<ManualImportController>(nextScope.ServiceProvider);
        Task<Microsoft.AspNetCore.Mvc.ActionResult<object>>? waiting = null;
        Guid companionOperation = Guid.Empty;
        try
        {
            await Task.WhenAny(entered.Task, importing).WaitAsync(TimeSpan.FromSeconds(10));
            if (importing.IsCompleted)
            {
                await using var diagnostic = await factory.CreateDbContextAsync();
                Assert.Fail(System.Text.Json.JsonSerializer.Serialize(
                        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await importing).Result).Value)
                    + Environment.NewLine + string.Join(Environment.NewLine, companionMessages)
                    + Environment.NewLine + System.Text.Json.JsonSerializer.Serialize(
                        await diagnostic.FileMutationJournals.AsNoTracking().ToListAsync()));
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var paused = await factory.CreateDbContextAsync();
            var journal = await paused.FileMutationJournals.SingleAsync(item => item.DestinationPath == companionDestination);
            companionOperation = journal.OperationId;
            Assert.Equal(book.Id, journal.AudiobookId);
            Assert.Equal(FileMutationOwner.RegistrationCompanionFile, journal.AudiobookFileId);
            Assert.Equal(beforeSourceDelete ? FileMutationJournalState.SourceDeletionAuthorized
                : FileMutationJournalState.TargetIdentityPersisted, journal.State);
            Assert.Equal(1, await paused.AudiobookFiles.CountAsync());
            Assert.Equal(2, await paused.FileMutationJournals.CountAsync());
            Assert.False(File.Exists(incoming));
            Assert.Equal("selected audio", await File.ReadAllTextAsync(Path.Join(bookFolder, "Companion Book.mp3")));
            waiting = next.Start(new ManualImportRequestDto
            {
                Path = otherSource,
                Mode = "interactive",
                Action = FileAction.Move,
                Items = [new ManualImportItemDto { FullPath = otherIncoming, MatchedAudiobookId = otherBook.Id }]
            });
            Assert.False(waiting.IsCompleted);
            if (cancelImport) cancellation.Cancel();
            Assert.False(importing.IsCompleted);
            Assert.False(waiting.IsCompleted);
            Assert.True(File.Exists(companion));
            Assert.Equal("other audio", await File.ReadAllTextAsync(otherIncoming));
        }
        finally
        {
            release.TrySetResult();
            try { await importing; }
            finally { if (waiting != null) await waiting; }
        }
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await importing).Result);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal(1, payload.GetProperty("importedCount").GetInt32());
        Assert.Equal(cancelImport, payload.GetProperty("stoppedByCancellation").GetBoolean());
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await waiting!).Result);
        Assert.Equal(1, hits);
        var reconciler = _provider.GetRequiredService<IFileRenameRecoveryReconciler>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await reconciler.ReconcileAsync();
            Assert.Equal("selected audio", await File.ReadAllTextAsync(Path.Join(bookFolder, "Companion Book.mp3")));
            Assert.Equal("cover bytes", await File.ReadAllTextAsync(companionDestination));
            Assert.Equal(failPublication, File.Exists(companion));
            if (failPublication) Assert.Equal("cover bytes", await File.ReadAllTextAsync(companion));
        }
        await using var final = await factory.CreateDbContextAsync();
        var recovered = await final.FileMutationJournals.SingleAsync(item => item.OperationId == companionOperation);
        Assert.Equal(failPublication ? FileMutationJournalState.CompletedSourceRetained
            : FileMutationJournalState.Completed, recovered.State);
        Assert.Equal(book.Id, recovered.AudiobookId);
        Assert.Equal(FileMutationOwner.RegistrationCompanionFile, recovered.AudiobookFileId);
        Assert.Equal(2, await final.AudiobookFiles.CountAsync());
        Assert.Equal(Path.Join(bookFolder, "Companion Book.mp3"),
            (await final.AudiobookFiles.SingleAsync(file => file.AudiobookId == book.Id)).Path);
        Assert.Equal(bookFolder, (await final.Audiobooks.SingleAsync(item => item.Id == book.Id)).BasePath);
        var unrelatedOwner = await final.AudiobookFiles.SingleAsync(file => file.AudiobookId == otherBook.Id);
        Assert.Equal("other audio", await File.ReadAllTextAsync(unrelatedOwner.Path!));
        Assert.False(File.Exists(otherIncoming));
        Assert.Equal(3, await final.FileMutationJournals.CountAsync());
        AssertNoListenarrArtifacts(outputRoot);
        AssertNoListenarrArtifacts(otherRoot);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Start_AdditiveCompanionCommitInterrupted_RetainsEverySourceAndScopesRecovery(
        bool afterSave, bool failCommit, bool cancelImport)
    {
        var outputRoot = FileService.GetTempDirectory("additive-companion-out");
        var sourceRoot = FileService.GetTempDirectory("additive-companion-source");
        var bookFolder = Path.Join(outputRoot, "Additive Book");
        var destination = Path.Join(bookFolder, "cover.jpg");
        var pause = new PauseCompatibilityCompanionCommitInterceptor(destination, afterSave, failCommit);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "additive-companion.db")};Pooling=False")
            .AddInterceptors(pause).Options;
        var factory = new RelocationImportSqliteFactory(options);
        var resolver = new Mock<IFilePublicationCapabilityResolver>();
        resolver.Setup(service => service.ResolveAsync(It.IsAny<FileAction>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<FilePublicationSourceProof>(), It.IsAny<CancellationToken>(),
                It.IsAny<Guid?>(), It.IsAny<CompatibilityCleanupOwner>()))
            .ReturnsAsync((FileAction action, string _, string _, FilePublicationSourceProof _,
                CancellationToken _, Guid? _, CompatibilityCleanupOwner _) => FilePublicationPlan.Additive(action));
        Init(builder => builder.WithSingleton(resolver.Object)
            .WithSingleton<IDbContextFactory<ListenArrDbContext>>(factory)
            .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => factory.CreateDbContext()),
                ServiceDescriptor.Scoped<ManualImportCompanionImporter, ManualImportCompanionImporter>()));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }
        await AddAuthorizedRootAsync(outputRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "{Title}";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var book = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Additive Book",
            Authors = ["Author"]
        });
        var incoming = await FileService.GetFileAsync(sourceRoot, "selected.mp3", "selected audio");
        var companion = await FileService.GetFileAsync(sourceRoot, "cover.jpg", "cover bytes");
        using var importScope = _provider.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(importScope.ServiceProvider);
        var importing = controller.Start(new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            IncludeCompanionFiles = true,
            Items = [new ManualImportItemDto { FullPath = incoming, MatchedAudiobookId = book.Id }]
        }, cancellation.Token);
        try
        {
            await Task.WhenAny(pause.Entered.Task, importing).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(importing.IsCompleted);
            await pause.Entered.Task;
            await using var paused = await factory.CreateDbContextAsync();
            var journal = await paused.CompatibilityFilePublicationJournals.SingleAsync(item => item.IsCompanionFile);
            Assert.Equal(afterSave ? CompatibilityFilePublicationState.RegistrationCommitted
                : CompatibilityFilePublicationState.TargetVerified, journal.State);
            Assert.Equal(afterSave ? book.Id : (int?)null, journal.AudiobookId);
            Assert.Equal(1, await paused.AudiobookFiles.CountAsync());
            Assert.Equal(2, await paused.CompatibilityFilePublicationJournals.CountAsync());
            Assert.Empty(await paused.FileMutationJournals.ToListAsync());
            if (cancelImport) cancellation.Cancel();
            Assert.False(importing.IsCompleted);
            Assert.Equal("selected audio", await File.ReadAllTextAsync(incoming));
            Assert.Equal("cover bytes", await File.ReadAllTextAsync(companion));
        }
        finally
        {
            pause.Release.TrySetResult();
            await importing;
        }
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await importing).Result);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal(1, payload.GetProperty("importedCount").GetInt32());
        Assert.Equal(cancelImport, payload.GetProperty("stoppedByCancellation").GetBoolean());
        Assert.Equal(1, pause.Hits);
        var recovery = _provider.GetRequiredService<ICompatibilityFilePublicationRecoveryService>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await recovery.ReconcileAsync();
            Assert.Equal("selected audio", await File.ReadAllTextAsync(incoming));
            Assert.Equal("selected audio", await File.ReadAllTextAsync(Path.Join(bookFolder, "Additive Book.mp3")));
            Assert.Equal("cover bytes", await File.ReadAllTextAsync(companion));
            Assert.Equal("cover bytes", await File.ReadAllTextAsync(destination));
        }
        await using (var verification = await factory.CreateDbContextAsync())
        {
            var journal = await verification.CompatibilityFilePublicationJournals.SingleAsync(item => item.IsCompanionFile);
            Assert.Equal(failCommit && !afterSave ? CompatibilityFilePublicationState.TargetVerified
                : CompatibilityFilePublicationState.Completed, journal.State);
            Assert.Equal(CompatibilitySourceDisposition.Retained, journal.SourceDisposition);
            var audioJournal = await verification.CompatibilityFilePublicationJournals.SingleAsync(item => !item.IsCompanionFile);
            Assert.Equal(CompatibilityFilePublicationState.Completed, audioJournal.State);
            Assert.Equal(bookFolder, (await verification.Audiobooks.SingleAsync()).BasePath);
            Assert.Equal(Path.Join(bookFolder, "Additive Book.mp3"), (await verification.AudiobookFiles.SingleAsync()).Path);
        }
        var otherRoot = FileService.GetTempDirectory("additive-companion-other");
        var otherSource = FileService.GetTempDirectory("additive-companion-other-source");
        await AddAuthorizedRootAsync(otherRoot);
        var otherIncoming = await FileService.GetFileAsync(otherSource, "other.mp3", "other audio");
        var otherBook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Other Book",
            BasePath = otherRoot,
            Authors = ["Author"]
        });
        using var nextScope = _provider.CreateScope();
        var next = ActivatorUtilities.CreateInstance<ManualImportController>(nextScope.ServiceProvider);
        var nextResponse = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await next.Start(new ManualImportRequestDto
        {
            Path = otherSource,
            Mode = "interactive",
            Action = FileAction.Move,
            Items = [new ManualImportItemDto { FullPath = otherIncoming, MatchedAudiobookId = otherBook.Id }]
        })).Result);
        var nextPayload = System.Text.Json.JsonSerializer.SerializeToElement(nextResponse.Value);
        Assert.True(nextPayload.GetProperty("importedCount").GetInt32() == 1,
            nextPayload.GetRawText());
        Assert.Equal("other audio", await File.ReadAllTextAsync(otherIncoming));
        await using var final = await factory.CreateDbContextAsync();
        Assert.Equal(2, await final.AudiobookFiles.CountAsync());
        Assert.Equal(3, await final.CompatibilityFilePublicationJournals.CountAsync());
        Assert.Equal("other audio", await File.ReadAllTextAsync(
            (await final.AudiobookFiles.SingleAsync(file => file.AudiobookId == otherBook.Id)).Path!));
        AssertNoListenarrArtifacts(outputRoot);
        AssertNoListenarrArtifacts(otherRoot);
    }

    private sealed class PauseCompatibilityCompanionCommitInterceptor(
        string destination, bool afterSave, bool failCommit) : SaveChangesInterceptor
    {
        private int _armed = 1;
        public int Hits { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!afterSave) await PauseAsync(eventData.Context);
            return result;
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (afterSave) await PauseAsync(eventData.Context);
            return result;
        }

        private async Task PauseAsync(DbContext? context)
        {
            if (context?.ChangeTracker.Entries<CompatibilityFilePublicationJournal>().Any(entry =>
                    entry.Entity.DestinationPath == destination && entry.Entity.IsCompanionFile
                    && entry.Entity.State == CompatibilityFilePublicationState.RegistrationCommitted) == true
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Hits++;
                Entered.TrySetResult();
                await Release.Task;
                if (failCommit) throw new IOException("Injected additive companion commit failure.");
            }
        }
    }

    private sealed class PauseImportOwnerCommitInterceptor(
        string destination, bool afterCommit, bool failCommit = true, bool ignoreCancellation = false) : DbTransactionInterceptor
    {
        private int _armed = 1;
        public int Hits { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (!afterCommit) await PauseAndFailAsync(eventData.Context, cancellationToken);
            return result;
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            afterCommit ? PauseAndFailAsync(eventData.Context, cancellationToken) : Task.CompletedTask;

        private async Task PauseAndFailAsync(DbContext? context, CancellationToken cancellationToken)
        {
            if (context?.ChangeTracker.Entries<AudiobookFile>().Any(entry => entry.Entity.Path == destination) == true)
            {
                await FailOnceAsync("Injected import owner commit failure.", cancellationToken);
            }
        }

        public Task FailPublicationAsync() =>
            FailOnceAsync("Injected import publication failure.", CancellationToken.None);

        private async Task FailOnceAsync(string message, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Hits++;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
                if (failCommit) throw new IOException(message);
            }
        }
    }

    private sealed class PauseRecoveryCommitInterceptor : DbCommandInterceptor
    {
        private int _armed;
        public bool FailAfterRelease { get; init; }
        public int FailureStage { get; init; }
        public int AffectedRows { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailureStage == 0 && IsJournalUpdate(command)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (FailAfterRelease) throw new IOException("Injected recovery journal update failure.");
            }
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (FailureStage != 0 && IsJournalUpdate(command))
            {
                var whereIndex = command.CommandText.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
                var assignments = whereIndex < 0 ? command.CommandText : command.CommandText[..whereIndex];
                var selectedColumn = FailureStage == 1 ? "\"AudiobookId\"" : "\"State\"";
                if (assignments.Contains(selectedColumn, StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
                {
                    AffectedRows = result;
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                    if (FailAfterRelease) throw new IOException("Injected recovery journal update failure.");
                }
            }
            return result;
        }

        private static bool IsJournalUpdate(DbCommand command) =>
            command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("FileMutationJournals", StringComparison.Ordinal);
    }

    private sealed class RelocationImportSqliteFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static void AssertNoListenarrArtifacts(string root)
    {
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr",
                StringComparison.OrdinalIgnoreCase));
    }
}
