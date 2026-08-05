/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files
{
    public class AudioFileServiceTests : BaseTests
    {
        private Audiobook _audiobook = new AudiobookBuilder().WithTitle("Generic book").WithAuthor("Random guy").Build();

        public override async Task InitializeAsync()
        {
            var metadataMock = new Mock<IMetadataService>();
            var metadata = new AudioMetadata
            {
                Duration = TimeSpan.FromSeconds(1234),
                Format = "m4b",
                BitRate = 64000,
                SampleRate = 32000,
                Channels = 1
            };
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(metadata);
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(
                    It.IsAny<MetadataFileSource>()))
                .ReturnsAsync(metadata);
            _services.AddSingleton(metadataMock.Object);
            Init();
            await _audiobookRepository.AddAsync(_audiobook);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_NullAudiobook_ThrowsArgumentNullException()
        {
            var service = _provider.GetRequiredService<IAudiobookFileService>();

            var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.EnsureAudiobookFileAsync(null!, "unused.m4b"));

            Assert.Equal("audiobook", exception.ParamName);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RegistrationLeaseWithNullAudiobook_ThrowsArgumentNullException()
        {
            var service = _provider.GetRequiredService<IAudiobookFileService>();
            using var registrationLease = new SequencedRegistrationLease(
                "unused.m4b",
                "unused-physical-identity",
                [true]);

            var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.EnsureAudiobookFileAsync(null!, registrationLease));

            Assert.Equal("audiobook", exception.ParamName);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_CreatesFileRecord_HappyPath()
        {
            var testFile = Path.Join(Path.GetTempPath(), $"afs-test-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(testFile, "dummy");
            _audiobook.BasePath = Path.GetDirectoryName(testFile);
            await _audiobookRepository.UpdateAsync(_audiobook);
            var created = await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(_audiobook, testFile, "test");
            Assert.True(created);
            var file = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(_audiobook.Id));
            Assert.Equal(testFile, file.Path);
            Assert.Equal("m4b", file.Format);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_HandlesUniqueConstraintViolation_ReturnsFalse()
        {
            var result = await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(_audiobook, "C:\\fake\\path.m4b", "test");
            Assert.False(result);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RefusesFileOutsideAudiobookFolder_AndCreatesHistory()
        {
            var bookA = new Audiobook { Title = "Book A", Authors = ["Author"], FilePath = Path.Join(Path.GetTempPath(), "Author", "BookA", "track1.m4b") };
            await _audiobookRepository.AddAsync(bookA);
            Directory.CreateDirectory(Path.GetDirectoryName(bookA.FilePath)!);
            var rejectedDir = Path.Join(Path.GetTempPath(), "Author", "BookB");
            Directory.CreateDirectory(rejectedDir);
            var rejectedFile = Path.Join(rejectedDir, $"rejected-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(rejectedFile, "dummy");
            var result = await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(bookA, rejectedFile, "test-scan");
            Assert.False(result);
            Assert.Contains(await _historyRepository.GetByAudiobookIdAsync(bookA.Id), h => h.EventType == "File Association Refused");
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_BasePathTakesPrecedenceOverStaleLegacyFilePath()
        {
            var oldBasePath = FileService.GetTempDirectory("audio-file-stale-legacy-old");
            var newBasePath = FileService.GetTempDirectory("audio-file-stale-legacy-new");
            var legacyFilePath = Path.Join(oldBasePath, "legacy.m4b");
            var candidateFile = Path.Join(oldBasePath, "candidate.m4b");
            await File.WriteAllTextAsync(legacyFilePath, "legacy");
            await File.WriteAllTextAsync(candidateFile, "candidate");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Stale Legacy Path",
                BasePath = newBasePath,
                FilePath = legacyFilePath
            });

            var created = await _provider
                .GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, candidateFile, "test-scan");

            Assert.False(created);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_BasePathOnly_RefusesFileOutsideAudiobookFolder()
        {
            var basePath = FileService.GetTempDirectory("audio-file-base-only");
            var outsidePath = FileService.GetTempDirectory("audio-file-base-only-outside");
            var candidateFile = Path.Join(outsidePath, "outside.m4b");
            await File.WriteAllTextAsync(candidateFile, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Base Path Only", BasePath = basePath });
            Assert.False(await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(audiobook, candidateFile, "test-scan"));
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        public async Task CheckAudiobookFileOwnershipAsync_NewAudiobookUsesPlannedBasePath()
        {
            var plannedBasePath = FileService.GetTempDirectory("audio-file-planned-base");
            var plannedFilePath = Path.Join(plannedBasePath, "planned.m4b");

            var result = await _provider
                .GetRequiredService<IAudiobookFileService>()
                .CheckAudiobookFileOwnershipAsync(
                    _audiobook,
                    plannedFilePath,
                    plannedBasePath);

            Assert.Equal(AudiobookFileOwnershipCheckOutcome.Available, result.Outcome);
            Assert.False(File.Exists(plannedFilePath));
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(_audiobook.Id));
        }

        [FileLinkFact]
        public async Task CheckAudiobookFileOwnershipAsync_BrokenSymlinkDestination_FailsClosed()
        {

            var plannedBasePath = FileService.GetTempDirectory("audio-file-broken-link-base");
            var plannedFilePath = Path.Join(plannedBasePath, "planned.m4b");
            File.CreateSymbolicLink(
                plannedFilePath,
                Path.Join(plannedBasePath, "missing-target.m4b"));
            _audiobook.BasePath = plannedBasePath;
            await _audiobookRepository.UpdateAsync(_audiobook);

            var result = await _provider
                .GetRequiredService<IAudiobookFileService>()
                .CheckAudiobookFileOwnershipAsync(
                    _audiobook,
                    plannedFilePath);

            Assert.Equal(AudiobookFileOwnershipCheckOutcome.IdentityUnavailable, result.Outcome);
        }

        [Fact]
        public async Task ClaimAudiobookFileAsync_WithoutAuthoritativeFolder_ReturnsIdentityUnavailable()
        {
            var candidateFile = await FileService.GetTempFileAsync($"boundaryless-{Guid.NewGuid():N}.m4b");
            var result = await _provider.GetRequiredService<IAudiobookFileService>().ClaimAudiobookFileAsync(_audiobook, AudiobookFile.CreateUnresolved(candidateFile), candidateFile);
            Assert.Equal(AudiobookFileClaimOutcome.IdentityUnavailable, result.Outcome);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(_audiobook.Id));
        }

        [Fact]
        public async Task ClaimAudiobookFileAsync_OutsideBasePath_ReturnsIdentityUnavailable()
        {
            var basePath = FileService.GetTempDirectory("audio-file-direct-claim-base");
            var outsidePath = FileService.GetTempDirectory("audio-file-direct-claim-outside");
            var candidateFile = Path.Join(outsidePath, "outside.m4b");
            await File.WriteAllTextAsync(candidateFile, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Direct Claim", BasePath = basePath });
            var result = await _provider.GetRequiredService<IAudiobookFileService>().ClaimAudiobookFileAsync(audiobook, AudiobookFile.CreateUnresolved(candidateFile), candidateFile);
            Assert.Equal(AudiobookFileClaimOutcome.IdentityUnavailable, result.Outcome);
        }

        [Fact]
        public async Task ClaimAudiobookFileAsync_RelativeLegacyPath_DoesNotAuthorizeWorkingDirectory()
        {
            var relativeDirectoryName = $"listenarr-relative-anchor-{Guid.NewGuid():N}";
            var workingDirectoryPath = Path.Join(Environment.CurrentDirectory, relativeDirectoryName);
            Directory.CreateDirectory(workingDirectoryPath);
            try
            {
                var candidateFile = Path.Join(workingDirectoryPath, "outside.m4b");
                await File.WriteAllTextAsync(candidateFile, "audio");
                var audiobook = await _audiobookRepository.AddAsync(new Audiobook
                {
                    Title = "Relative Legacy Anchor",
                    BasePath = FileService.GetTempDirectory("audio-file-relative-anchor-base"),
                    FilePath = Path.Join(relativeDirectoryName, "legacy.m4b")
                });
                var result = await _provider.GetRequiredService<IAudiobookFileService>().ClaimAudiobookFileAsync(audiobook, AudiobookFile.CreateUnresolved(candidateFile), candidateFile);
                Assert.Equal(AudiobookFileClaimOutcome.IdentityUnavailable, result.Outcome);
            }
            finally
            {
                Directory.Delete(workingDirectoryPath, true);
            }
        }

        [Fact]
        public async Task ClaimAudiobookFileAsync_StaleCallerCannotClaimFileUnderPreviousBasePath()
        {
            var oldBasePath = FileService.GetTempDirectory("audio-file-direct-stale-old");
            var candidate = Path.Join(oldBasePath, "stale-candidate.m4b");
            await File.WriteAllTextAsync(candidate, "candidate");
            var staleAudiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Direct Stale Caller", BasePath = oldBasePath });
            using (var scope = _provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                (await db.Audiobooks.SingleAsync(a => a.Id == staleAudiobook.Id)).BasePath = FileService.GetTempDirectory("audio-file-direct-stale-new");
                await db.SaveChangesAsync();
            }
            var result = await _provider.GetRequiredService<IAudiobookFileService>().ClaimAudiobookFileAsync(staleAudiobook, AudiobookFile.CreateUnresolved(candidate), candidate);
            Assert.Equal(AudiobookFileClaimOutcome.IdentityUnavailable, result.Outcome);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_StaleCallerCannotRegisterFileUnderPreviousBasePath()
        {
            var oldBasePath = FileService.GetTempDirectory("audio-file-stale-old");
            var oldFilePath = Path.Join(oldBasePath, "existing.m4b");
            var candidate = Path.Join(oldBasePath, "stale-candidate.m4b");
            await File.WriteAllTextAsync(oldFilePath, "old");
            await File.WriteAllTextAsync(candidate, "candidate");
            var staleAudiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Stale Caller", BasePath = oldBasePath, FilePath = oldFilePath });
            using (var scope = _provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var current = await db.Audiobooks.SingleAsync(a => a.Id == staleAudiobook.Id);
                current.BasePath = FileService.GetTempDirectory("audio-file-stale-new");
                current.FilePath = Path.Join(current.BasePath, "current.m4b");
                await db.SaveChangesAsync();
            }
            Assert.False(await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(staleAudiobook, candidate, "stale-caller"));
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_AllowsFileWithinBasePath_WhenBasePathHasTrailingSeparator()
        {
            var oldDir = Path.Join(Path.GetTempPath(), "listenarr-audiofile-old", Guid.NewGuid().ToString(), "Old Folder");
            Directory.CreateDirectory(oldDir);
            var oldFile = Path.Join(oldDir, "track1.m4b");
            await File.WriteAllTextAsync(oldFile, "old");
            var importDir = Path.Join(Path.GetTempPath(), "listenarr-audiofile-new", Guid.NewGuid().ToString(), "Jack of Shadows");
            Directory.CreateDirectory(importDir);
            var candidate = Path.Join(importDir, "Jack of Shadows_ Rediscovered Classics, Book 23-14.mp3");
            await File.WriteAllTextAsync(candidate, "new");
            var book = await _audiobookRepository.AddAsync(new Audiobook { Title = "Jack of Shadows", FilePath = oldFile, BasePath = importDir + Path.DirectorySeparatorChar });
            Assert.True(await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(book, candidate, "test-scan"));
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivityMode.Sensitive, false)]
        [InlineData(FileSystemCaseSensitivityMode.Insensitive, true)]
        public async Task EnsureAudiobookFileAsync_ExistingDirectoryContainmentUsesResolvedRootSemantics(FileSystemCaseSensitivityMode mode, bool shouldCreate)
        {
            var rootPath = FileService.GetTempDirectory("audio-file-semantics");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder().WithName("Audio Root").WithPath(rootPath).WithCaseSensitivityMode(mode).WithIsDefault().Build());
            var candidateDir = Path.Join(rootPath, "casebook");
            Directory.CreateDirectory(candidateDir);
            var candidate = Path.Join(candidateDir, "track.m4b");
            await File.WriteAllTextAsync(candidate, "audio");
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Case Book").WithFilePath(Path.Join(rootPath, "CaseBook", "existing.m4b")).Build());
            Assert.Equal(shouldCreate, await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(book, candidate, "test-scan"));
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivityMode.Sensitive, AudiobookFileOwnershipCheckOutcome.IdentityUnavailable)]
        [InlineData(FileSystemCaseSensitivityMode.Insensitive, AudiobookFileOwnershipCheckOutcome.Available)]
        public async Task CheckAudiobookFileOwnershipAsync_ExistingDirectoryContainmentUsesResolvedRootSemantics(
            FileSystemCaseSensitivityMode mode,
            AudiobookFileOwnershipCheckOutcome expectedOutcome)
        {
            var rootPath = FileService.GetTempDirectory("audio-file-ownership-semantics");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Ownership Audio Root")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(mode)
                .Build());
            var candidateDirectory = Path.Join(rootPath, "casebook");
            Directory.CreateDirectory(candidateDirectory);
            var candidate = Path.Join(candidateDirectory, "track.m4b");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Ownership Case Book")
                .WithFilePath(Path.Join(rootPath, "CaseBook", "existing.m4b"))
                .Build());

            var result = await _provider.GetRequiredService<IAudiobookFileService>()
                .CheckAudiobookFileOwnershipAsync(audiobook, candidate);

            Assert.Equal(expectedOutcome, result.Outcome);
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivityMode.Sensitive, AudiobookFileClaimOutcome.IdentityUnavailable)]
        [InlineData(FileSystemCaseSensitivityMode.Insensitive, AudiobookFileClaimOutcome.Created)]
        public async Task ClaimAudiobookFileAsync_ExistingDirectoryContainmentUsesResolvedRootSemantics(
            FileSystemCaseSensitivityMode mode,
            AudiobookFileClaimOutcome expectedOutcome)
        {
            var rootPath = FileService.GetTempDirectory("audio-file-claim-semantics");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Claim Audio Root")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(mode)
                .Build());
            var candidateDirectory = Path.Join(rootPath, "casebook");
            Directory.CreateDirectory(candidateDirectory);
            var candidate = Path.Join(candidateDirectory, "track.m4b");
            await File.WriteAllTextAsync(candidate, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Claim Case Book")
                .WithFilePath(Path.Join(rootPath, "CaseBook", "existing.m4b"))
                .Build());

            var result = await _provider.GetRequiredService<IAudiobookFileService>()
                .ClaimAudiobookFileAsync(
                    audiobook,
                    AudiobookFile.CreateUnresolved(candidate),
                    candidate);

            Assert.Equal(expectedOutcome, result.Outcome);
        }

        [DirectoryLinkFact]
        public async Task EnsureAudiobookFileAsync_CaseInsensitiveAliasSymlinkToSibling_FailsClosed()
        {

            var (audiobook, candidate) = await CreateCaseInsensitiveAliasSymlinkEscapeAsync("ensure");

            var created = await _provider.GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, candidate, "test-scan");

            Assert.False(created);
        }

        [DirectoryLinkFact]
        public async Task CheckAudiobookFileOwnershipAsync_CaseInsensitiveAliasSymlinkToSibling_FailsClosed()
        {

            var (audiobook, candidate) = await CreateCaseInsensitiveAliasSymlinkEscapeAsync("ownership");

            var result = await _provider.GetRequiredService<IAudiobookFileService>()
                .CheckAudiobookFileOwnershipAsync(audiobook, candidate);

            Assert.Equal(AudiobookFileOwnershipCheckOutcome.IdentityUnavailable, result.Outcome);
        }

        [DirectoryLinkFact]
        public async Task EnsureAudiobookFileAsync_CaseInsensitiveAliasRootSymlinkToSibling_FailsClosed()
        {

            var (audiobook, candidate) = await CreateCaseInsensitiveAliasRootSymlinkEscapeAsync("ensure-root");

            var created = await _provider.GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, candidate, "test-scan");

            Assert.False(created);
        }

        [DirectoryLinkFact]
        public async Task CheckAudiobookFileOwnershipAsync_CaseInsensitiveAliasRootSymlinkToSibling_FailsClosed()
        {

            var (audiobook, candidate) = await CreateCaseInsensitiveAliasRootSymlinkEscapeAsync("ownership-root");

            var result = await _provider.GetRequiredService<IAudiobookFileService>()
                .CheckAudiobookFileOwnershipAsync(audiobook, candidate);

            Assert.Equal(AudiobookFileOwnershipCheckOutcome.IdentityUnavailable, result.Outcome);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_NestedRootUsesMostSpecificCaseSemantics()
        {
            var outerRoot = FileService.GetTempDirectory("audio-file-nested-semantics");
            var innerRoot = Path.Join(outerRoot, "Sensitive Library");
            Directory.CreateDirectory(innerRoot);
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("A Outer Root")
                .WithPath(outerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Z Nested Root")
                .WithPath(innerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
                .Build());
            var candidateDirectory = Path.Join(innerRoot, "casebook");
            Directory.CreateDirectory(candidateDirectory);
            var candidate = Path.Join(candidateDirectory, "track.m4b");
            await File.WriteAllTextAsync(candidate, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Nested Case Book",
                FilePath = Path.Join(innerRoot, "CaseBook", "existing.m4b")
            });

            var created = await _provider.GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, candidate, "test-scan");

            Assert.False(created);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_NestedInsensitiveRootOverridesSensitiveParent()
        {
            var outerRoot = FileService.GetTempDirectory("audio-file-nested-insensitive-semantics");
            var innerRoot = Path.Join(outerRoot, "Insensitive Library");
            Directory.CreateDirectory(innerRoot);
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("A Sensitive Outer Root")
                .WithPath(outerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Z Insensitive Nested Root")
                .WithPath(innerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());
            var candidateDirectory = Path.Join(innerRoot, "casebook");
            Directory.CreateDirectory(candidateDirectory);
            var candidate = Path.Join(candidateDirectory, "track.m4b");
            await File.WriteAllTextAsync(candidate, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Nested Insensitive Case Book",
                FilePath = Path.Join(innerRoot, "CaseBook", "existing.m4b")
            });

            var created = await _provider.GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, candidate, "test-scan");

            Assert.True(created);
        }

        [DirectoryLinkFact]
        public async Task EnsureAudiobookFileAsync_SymlinkedDirectoryEscapesBasePath_FailsClosed()
        {
            var basePath = FileService.GetTempDirectory("audio-file-link-base");
            var outside = FileService.GetTempDirectory("audio-file-link-outside");
            await File.WriteAllTextAsync(Path.Join(outside, "outside.m4b"), "audio");
            Directory.CreateSymbolicLink(Path.Join(basePath, "linked"), outside);
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Linked Escape", BasePath = basePath });
            Assert.False(await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(audiobook, Path.Join(basePath, "linked", "outside.m4b"), "test-scan"));
        }

        [WindowsFact]
        public async Task EnsureAudiobookFileAsync_ForeignPersistedBasePath_DoesNotAuthorizeWindowsAlias()
        {
            var nativeBase = FileService.GetWindowsRootRelativeTempDirectory(
                "audio-file-foreign-persisted-base");
            var candidate = Path.Join(nativeBase, "track.m4b");
            await File.WriteAllTextAsync(candidate, "audio");
            var foreignBase = TempFileService
                .GetWindowsRootRelativeForeignAlias(nativeBase);
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Foreign Persisted Base",
                BasePath = foreignBase
            });

            var created = await _provider.GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, candidate, "test-scan");

            Assert.False(created);
            Assert.True(File.Exists(candidate));
            Assert.Empty(
                await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_InvalidStoredContainmentPath_FailsClosed()
        {
            var candidateDir = FileService.GetTempDirectory("audio-file-invalid-containment");
            var candidate = Path.Join(candidateDir, "track.m4b");
            await File.WriteAllTextAsync(candidate, "audio");
            var invalid = $"invalid{(char)0}path.m4b";
            var book = await _audiobookRepository.AddAsync(new Audiobook { Title = "Invalid Containment", BasePath = invalid, FilePath = invalid });
            Assert.False(await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(book, candidate, "test-scan"));
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RootFolderLookupFailure_FailsClosed()
        {
            var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
            rootFolderService.Setup(service => service.GetAllAsync())
                .ThrowsAsync(new InvalidOperationException("Root folder lookup failed."));
            Init(builder => builder.WithSingleton<IRootFolderService>(rootFolderService.Object));

            var audiobookDirectory = FileService.GetTempDirectory("audio-file-root-lookup-failure");
            var candidate = Path.Join(audiobookDirectory, "track.m4b");
            await File.WriteAllTextAsync(candidate, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Root Lookup Failure")
                .WithFilePath(Path.Join(audiobookDirectory, "existing.m4b"))
                .Build());

            var created = await _provider.GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, candidate, "test-scan");

            Assert.False(created);
        }

        private async Task<(Audiobook Audiobook, string CandidatePath)> CreateCaseInsensitiveAliasSymlinkEscapeAsync(
            string scenario)
        {
            var rootPath = FileService.GetTempDirectory($"audio-file-case-link-{scenario}");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName($"Case Link Root {scenario}")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());
            var candidateDirectory = Path.Join(rootPath, "casebook");
            var siblingDirectory = Path.Join(rootPath, "OtherBook");
            Directory.CreateDirectory(candidateDirectory);
            Directory.CreateDirectory(siblingDirectory);
            var siblingFile = Path.Join(siblingDirectory, "other.m4b");
            await File.WriteAllTextAsync(siblingFile, "audio");
            Directory.CreateSymbolicLink(
                Path.Join(candidateDirectory, "linked"),
                siblingDirectory);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle($"Case Link Book {scenario}")
                .WithFilePath(Path.Join(rootPath, "CaseBook", "existing.m4b"))
                .Build());
            return (audiobook, Path.Join(candidateDirectory, "linked", "other.m4b"));
        }

        private async Task<(Audiobook Audiobook, string CandidatePath)> CreateCaseInsensitiveAliasRootSymlinkEscapeAsync(
            string scenario)
        {
            var rootPath = FileService.GetTempDirectory($"audio-file-case-root-link-{scenario}");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName($"Case Root Link {scenario}")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());
            var siblingDirectory = Path.Join(rootPath, "OtherBook");
            Directory.CreateDirectory(siblingDirectory);
            var siblingFile = Path.Join(siblingDirectory, "other.m4b");
            await File.WriteAllTextAsync(siblingFile, "audio");
            var candidateDirectory = Path.Join(rootPath, "casebook");
            Directory.CreateSymbolicLink(candidateDirectory, siblingDirectory);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle($"Case Root Link Book {scenario}")
                .WithFilePath(Path.Join(rootPath, "CaseBook", "existing.m4b"))
                .Build());
            return (audiobook, Path.Join(candidateDirectory, "other.m4b"));
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RejectsNonAudioFile()
        {
            var testFile = await FileService.GetTempFileAsync($"afs-test-{Guid.NewGuid()}.jpg");
            Assert.False(await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(_audiobook, testFile, "test"));
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RequestCanceledAfterClaim_RollsBackExactGeneration()
        {
            var testFile = await FileService.GetTempFileAsync(
                $"physical-generation-canceled-rollback-{Guid.NewGuid():N}.m4b");
            _audiobook.BasePath = Path.GetDirectoryName(testFile);
            await _audiobookRepository.UpdateAsync(_audiobook);
            using var cancellation = new CancellationTokenSource();
            using var registrationLease = new SequencedRegistrationLease(
                testFile,
                "canceled-physical-generation",
                [true, true, false],
                publicationCheck =>
                {
                    if (publicationCheck == 3)
                    {
                        cancellation.Cancel();
                    }
                });

            var created = await _provider
                .GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(
                    _audiobook,
                    registrationLease,
                    "canceled-import",
                    cancellation.Token);

            Assert.False(created);
            Assert.Empty(await _audiobookFileRepository
                .GetByAudiobookIdAsync(_audiobook.Id));
        }

        [Fact]
        public async Task RefreshPhysicalGenerationAsync_PublicationChangesAfterDatabaseUpdate_RestoresPredecessor()
        {
            var testFile = await FileService.GetTempFileAsync(
                $"physical-generation-rollback-{Guid.NewGuid():N}.m4b");
            _audiobook.BasePath = Path.GetDirectoryName(testFile);
            await _audiobookRepository.UpdateAsync(_audiobook);
            var service = _provider.GetRequiredService<IAudiobookFileService>();
            using (var initialLease =
                PinnedAudiobookFileRegistrationLease.Open(testFile))
            {
                Assert.True(await service.EnsureAudiobookFileAsync(
                    _audiobook,
                    initialLease,
                    "initial"));
            }

            var predecessor = Assert.Single(
                await _audiobookFileRepository.GetByAudiobookIdAsync(
                    _audiobook.Id));
            var predecessorIdentity = predecessor.PhysicalObjectIdentity;
            Assert.False(string.IsNullOrWhiteSpace(predecessorIdentity));
            using var replacementLease = new SequencedRegistrationLease(
                testFile,
                "replacement-physical-identity",
                [true, true, true, false]);

            var refreshed = await service.RefreshPhysicalGenerationAsync(
                _audiobook,
                predecessor.Id,
                predecessorIdentity,
                replacementLease,
                "replacement");

            Assert.False(refreshed);
            var persisted = Assert.Single(
                await _audiobookFileRepository.GetByAudiobookIdAsync(
                    _audiobook.Id));
            Assert.Equal(predecessor.Id, persisted.Id);
            Assert.Equal(predecessorIdentity, persisted.PhysicalObjectIdentity);
            Assert.Equal(predecessor.Format, persisted.Format);
            Assert.Equal(predecessor.DurationSeconds, persisted.DurationSeconds);
            Assert.Equal(predecessor.Source, persisted.Source);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_PersistsMetadataFromMetadataService()
        {
            var testFile = await FileService.GetTempFileAsync($"meta-int-{Guid.NewGuid()}.m4b");
            _audiobook.BasePath = Path.GetDirectoryName(testFile);
            await _audiobookRepository.UpdateAsync(_audiobook);
            Assert.True(await _provider.GetRequiredService<IAudiobookFileService>().EnsureAudiobookFileAsync(_audiobook, testFile, "test"));
            var file = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(_audiobook.Id));
            Assert.Equal(1234, (int)file.DurationSeconds!.Value);
            Assert.Equal("m4b", file.Format);
            Assert.Equal(64000, file.Bitrate);
            Assert.Equal(32000, file.SampleRate);
            Assert.Equal(1, file.Channels);
        }

        private sealed class SequencedRegistrationLease(
            string path,
            string physicalObjectIdentity,
            IEnumerable<bool> publicationMatches,
            Action<int>? onPublicationCheck = null) :
            IAudiobookFileRegistrationLease
        {
            private readonly Queue<bool> _publicationMatches =
                new(publicationMatches);
            private int _publicationChecks;

            public string PublicPath { get; } = path;
            public string MetadataPath { get; } = path;
            public string PhysicalObjectIdentity { get; } =
                physicalObjectIdentity;
            public string? SourcePhysicalObjectIdentity => null;

            public bool MatchesCurrentPublication()
            {
                var publicationCheck = Interlocked.Increment(
                    ref _publicationChecks);
                onPublicationCheck?.Invoke(publicationCheck);
                return _publicationMatches.Count == 0
                    ? false
                    : _publicationMatches.Dequeue();
            }

            public bool PrepareCleanupRecovery(int audiobookId) => true;

            public RegistrationPublicationCompletion CompletePublication() =>
                RegistrationPublicationCompletion.Completed;

            public async Task<bool> MatchesContentAsync(
                Stream candidateStream,
                CancellationToken cancellationToken = default)
            {
                await using var publishedStream = File.OpenRead(PublicPath);
                if (candidateStream.CanSeek)
                {
                    candidateStream.Position = 0;
                }

                var candidateHash = await System.Security.Cryptography.SHA256
                    .HashDataAsync(candidateStream, cancellationToken);
                var publishedHash = await System.Security.Cryptography.SHA256
                    .HashDataAsync(publishedStream, cancellationToken);
                return candidateHash.AsSpan().SequenceEqual(publishedHash);
            }

            public void Dispose()
            {
            }
        }
    }
}
