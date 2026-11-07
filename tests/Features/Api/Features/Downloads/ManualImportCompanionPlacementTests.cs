/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

/// <summary>
/// Where the manual-import companion pass puts the files it sweeps up.
///
/// It used to relativize each companion against the request's <c>path</c> and re-root that under
/// the destination, so a request whose <c>path</c> sat above the selected files recreated every
/// directory in between inside the book folder. These pin the rule that replaced it rather than
/// the symptom that exposed it: a companion goes where the file it accompanies went, and nowhere
/// else. The audio destination is built from the naming pattern and carries none of the source's
/// shape, so there is no source structure here for a sidecar to keep.
/// </summary>
[Trait("Name", "ManualImportCompanionPlacementTests")]
[Trait("Category", "Unit")]
public sealed class ManualImportCompanionPlacementTests : BaseTests
{
    private sealed record CompanionOutcome(
        int Imported,
        IReadOnlyList<string> Destinations);

    /// <summary>
    /// Drives one companion pass over real files on disk, with the publication and ownership
    /// collaborators stubbed out, and reports the destination paths the pass asked the mover to
    /// publish to. Nothing is moved: the destination path is the claim under test.
    /// </summary>
    private static async Task<CompanionOutcome> RunPassAsync(
        string requestPath,
        IReadOnlyList<(string Source, string Destination, bool Success)> selected,
        string audiobookBasePath)
    {
        var destinations = new List<string>();
        var audiobook = new Audiobook { Id = 77, BasePath = audiobookBasePath };

        var lease = new Mock<IAudiobookFileRegistrationLease>();
        lease.Setup(instance => instance.PrepareCleanupRecovery(It.IsAny<int>())).Returns(true);
        lease.Setup(instance => instance.CompletePublication())
            .Returns(RegistrationPublicationCompletion.Completed);

        var mover = new Mock<IFileMover>();
        mover
            .Setup(service => service.PrepareActionForRegistrationDetailedAsync(
                It.IsAny<FilePublicationPlan>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<FilePublicationSourceProof>(),
                It.IsAny<bool>(),
                It.IsAny<int?>()))
            .Callback<FilePublicationPlan, string, string, Guid, string?,
                FilePublicationSourceProof, bool, int?>(
                (_, _, destination, _, _, _, _, _) => destinations.Add(destination))
            .ReturnsAsync(new FilePublicationPreparationResult(
                FilePublicationOutcome.Success,
                FileAction.Copy,
                FileAction.Copy,
                FilePublicationSourceDisposition.Unchanged,
                lease.Object));

        var sourceCapability = new Mock<IFilePublicationSourceCapability>();
        sourceCapability
            .Setup(service => service.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilePublicationSourceCapabilityResult.SupportedForProof(
                new FilePublicationSourceProof("test-source-generation", 1, new string('A', 64))));

        var fileService = new Mock<IAudiobookFileService>();
        fileService
            .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                AudiobookFileOwnershipCheckOutcome.Available));

        var ownershipStore = new Mock<ILibraryDirectoryOwnershipStore>();
        ownershipStore
            .Setup(store => store.EnsureCreatedHierarchyAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        ownershipStore
            .Setup(store => store.EnsureAdditiveHierarchyAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var importer = new ManualImportCompanionImporter(
            Mock.Of<IMetadataService>(),
            mover.Object,
            sourceCapability.Object,
            new LocalFileSystem(),
            ownershipStore.Object,
            NullLogger<ManualImportCompanionImporter>.Instance,
            fileService.Object);

        var semanticsResolver = new FileSystemSemanticsResolver();
        var sourceResolution = await semanticsResolver.ResolveAsync(requestPath);
        var destinationResolution = await semanticsResolver.ResolveAsync(audiobookBasePath);
        Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
        Assert.Equal(PathIdentityState.Valid, destinationResolution.State);

        var items = selected
            .Select(entry => new ManualImportItemDto
            {
                FullPath = entry.Source,
                MatchedAudiobookId = audiobook.Id
            })
            .ToList();
        var results = selected
            .Select(entry => new ManualImportResultDto
            {
                Success = entry.Success,
                Error = entry.Success ? null : "the harness failed this item deliberately",
                SourcePath = entry.Source,
                DestinationPath = entry.Success ? entry.Destination : null,
                Audiobook = audiobook
            })
            .ToList();

        var imported = await importer.ImportAsync(
            FileAction.Copy,
            items,
            results,
            requestPath,
            selectedAudioProfiles: [],
            new ManualImportDestinationTracker(
                new LocalFileSystem(),
                Mock.Of<IFilePublicationSourceCapability>()),
            sourceResolution.Semantics,
            new Dictionary<int, FileSystemSemanticsResolution>
            {
                [audiobook.Id] = destinationResolution
            },
            importBlacklist: []);

        return new CompanionOutcome(imported, destinations);
    }

    private static string NewTestRoot(string name) => Path.Join(
        Path.GetTempPath(),
        "listenarr-tests",
        $"manual-companion-placement-{name}-{Guid.NewGuid():N}");

    private static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// The defect. The request's path is the folder the user browsed to; the audio sits three
    /// directories below it. Before the fix the companion was relativized against the request
    /// path, so <c>src/Arthur Conan Doyle/2006 - The Valley of Fear/</c> was recreated inside the
    /// book folder.
    /// </summary>
    [Fact]
    public async Task ImportAsync_RequestPathAboveTheSelectedFiles_DoesNotMirrorTheSourceTree()
    {
        var testRoot = NewTestRoot("ancestor");
        var requestPath = Path.Join(testRoot, "src");
        var selectedDirectory = Path.Join(
            requestPath,
            "Arthur Conan Doyle",
            "2006 - The Valley of Fear");
        var bookFolder = Path.Join(testRoot, "library", "The Valley of Fear");
        try
        {
            var audioSource = Path.Join(selectedDirectory, "book.m4b");
            var companionSource = Path.Join(selectedDirectory, "book.nfo");
            await WriteAsync(audioSource, "audio");
            await WriteAsync(companionSource, "sidecar");
            Directory.CreateDirectory(bookFolder);

            var outcome = await RunPassAsync(
                requestPath,
                [(audioSource, Path.Join(bookFolder, "The Valley of Fear.m4b"), true)],
                bookFolder);

            Assert.Equal(1, outcome.Imported);
            AssertPlacedAt(bookFolder, outcome.Destinations, "book.nfo");
            AssertNoDirectoriesUnder(bookFolder);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    /// <summary>
    /// The worst shape of the same defect. A request path of the bare filesystem root used to
    /// hand back the companion's own absolute path with the root stripped off, which no
    /// containment check can reject, so the whole source path appeared in the book folder. The
    /// request path no longer reaches the placement decision at all, which is why this passes
    /// now; nothing here exercises the resolver's bare-root backstop.
    /// </summary>
    [Fact]
    public async Task ImportAsync_RequestPathIsTheFilesystemRoot_StillPlacesCompanionBesideTheAudio()
    {
        var testRoot = NewTestRoot("bare-root");
        var selectedDirectory = Path.Join(testRoot, "downloads", "The.Release");
        var bookFolder = Path.Join(testRoot, "library", "The Valley of Fear");
        var filesystemRoot = Path.GetPathRoot(testRoot)
            ?? throw new InvalidOperationException("The host filesystem root is unavailable.");
        try
        {
            var audioSource = Path.Join(selectedDirectory, "book.m4b");
            var companionSource = Path.Join(selectedDirectory, "book.nfo");
            await WriteAsync(audioSource, "audio");
            await WriteAsync(companionSource, "sidecar");
            Directory.CreateDirectory(bookFolder);

            var outcome = await RunPassAsync(
                filesystemRoot,
                [(audioSource, Path.Join(bookFolder, "The Valley of Fear.m4b"), true)],
                bookFolder);

            Assert.Equal(1, outcome.Imported);
            AssertPlacedAt(bookFolder, outcome.Destinations, "book.nfo");
            AssertNoDirectoriesUnder(bookFolder);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    /// <summary>
    /// The companion follows the file it accompanies, wherever that file went. The source
    /// subdirectory is named <c>bonus</c> and the audio's destination subdirectory is named
    /// <c>extras</c>, so mirroring the source and following the audio give different answers and
    /// this fact can tell them apart. It is also the control against a blanket flatten into the
    /// book folder: a companion whose audio landed in a subdirectory belongs in that
    /// subdirectory.
    /// </summary>
    [Fact]
    public async Task ImportAsync_CompanionBesideAudioImportedIntoASubdirectory_FollowsItThere()
    {
        var testRoot = NewTestRoot("follows");
        var requestPath = Path.Join(testRoot, "src", "The.Release");
        var bonusDirectory = Path.Join(requestPath, "bonus");
        var bookFolder = Path.Join(testRoot, "library", "The Valley of Fear");
        try
        {
            var audioSource = Path.Join(requestPath, "book.m4b");
            var bonusAudioSource = Path.Join(bonusDirectory, "interview.m4b");
            var companionSource = Path.Join(bonusDirectory, "interview.nfo");
            await WriteAsync(audioSource, "audio");
            await WriteAsync(bonusAudioSource, "bonus audio");
            await WriteAsync(companionSource, "sidecar");
            Directory.CreateDirectory(bookFolder);

            var outcome = await RunPassAsync(
                requestPath,
                [
                    (audioSource, Path.Join(bookFolder, "The Valley of Fear.m4b"), true),
                    (bonusAudioSource, Path.Join(bookFolder, "extras", "interview.m4b"), true)
                ],
                bookFolder);

            Assert.Equal(1, outcome.Imported);
            AssertPlacedAt(
                bookFolder,
                outcome.Destinations,
                Path.Join("extras", "interview.nfo"));
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    /// <summary>
    /// Selected files in two directories that share nothing but the request path. Each companion
    /// follows its own audio rather than being described against a root computed across both.
    /// This is the case an earlier version of this fix got wrong: it resolved one source root for
    /// the whole batch, and a batch spanning two trees made that root shallow enough to mirror a
    /// directory for each of them.
    /// </summary>
    [Fact]
    public async Task ImportAsync_SelectedFilesInDisjointDirectories_EachCompanionFollowsItsOwnAudio()
    {
        var testRoot = NewTestRoot("disjoint");
        var requestPath = Path.Join(testRoot, "src");
        var firstDirectory = Path.Join(requestPath, "incoming", "The.Release");
        var secondDirectory = Path.Join(requestPath, "elsewhere", "Another.Release");
        var bookFolder = Path.Join(testRoot, "library", "The Valley of Fear");
        try
        {
            var firstAudio = Path.Join(firstDirectory, "book.m4b");
            var secondAudio = Path.Join(secondDirectory, "part2.m4b");
            await WriteAsync(firstAudio, "audio one");
            await WriteAsync(secondAudio, "audio two");
            await WriteAsync(Path.Join(firstDirectory, "book.nfo"), "sidecar one");
            await WriteAsync(Path.Join(secondDirectory, "part2.nfo"), "sidecar two");
            Directory.CreateDirectory(bookFolder);

            var outcome = await RunPassAsync(
                requestPath,
                [
                    (firstAudio, Path.Join(bookFolder, "The Valley of Fear.m4b"), true),
                    (secondAudio, Path.Join(bookFolder, "The Valley of Fear - 02.m4b"), true)
                ],
                bookFolder);

            Assert.Equal(2, outcome.Imported);
            AssertPlacedAt(
                bookFolder,
                [.. outcome.Destinations.OrderBy(destination => destination, StringComparer.Ordinal)],
                "book.nfo",
                "part2.nfo");
            AssertNoDirectoriesUnder(bookFolder);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    /// <summary>
    /// Nothing is invented. A companion whose directory produced no successful import has no
    /// file to travel with, so it is refused rather than placed somewhere plausible. The old rule
    /// placed it, because the request path alone was enough to describe a destination for it, and
    /// that left a sidecar in the library for a book file that never arrived.
    /// </summary>
    [Fact]
    public async Task ImportAsync_CompanionWhoseDirectoryImportedNothing_IsRefused()
    {
        var testRoot = NewTestRoot("orphan");
        var requestPath = Path.Join(testRoot, "src");
        var importedDirectory = Path.Join(requestPath, "The.Release");
        var failedDirectory = Path.Join(requestPath, "The.Other.Release");
        var bookFolder = Path.Join(testRoot, "library", "The Valley of Fear");
        try
        {
            var importedAudio = Path.Join(importedDirectory, "book.m4b");
            var failedAudio = Path.Join(failedDirectory, "part2.m4b");
            await WriteAsync(importedAudio, "audio");
            await WriteAsync(failedAudio, "audio that will not import");
            await WriteAsync(Path.Join(failedDirectory, "part2.nfo"), "orphan sidecar");
            Directory.CreateDirectory(bookFolder);

            var outcome = await RunPassAsync(
                requestPath,
                [
                    (importedAudio, Path.Join(bookFolder, "The Valley of Fear.m4b"), true),
                    (failedAudio, Path.Join(bookFolder, "The Valley of Fear - 02.m4b"), false)
                ],
                bookFolder);

            Assert.Equal(0, outcome.Imported);
            Assert.Empty(outcome.Destinations);
            AssertNoDirectoriesUnder(bookFolder);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    /// <summary>
    /// Control: nothing was refused wholesale. The ordinary shape, where the request path is the
    /// folder the audio is actually in, places the companion beside the audio exactly as before.
    /// </summary>
    [Fact]
    public async Task ImportAsync_RequestPathAtTheSelectedDirectory_PlacesCompanionBesideTheAudio()
    {
        var testRoot = NewTestRoot("plain");
        var requestPath = Path.Join(testRoot, "src", "The.Release");
        var bookFolder = Path.Join(testRoot, "library", "The Valley of Fear");
        try
        {
            var audioSource = Path.Join(requestPath, "book.m4b");
            var companionSource = Path.Join(requestPath, "book.nfo");
            await WriteAsync(audioSource, "audio");
            await WriteAsync(companionSource, "sidecar");
            Directory.CreateDirectory(bookFolder);

            var outcome = await RunPassAsync(
                requestPath,
                [(audioSource, Path.Join(bookFolder, "The Valley of Fear.m4b"), true)],
                bookFolder);

            Assert.Equal(1, outcome.Imported);
            AssertPlacedAt(bookFolder, outcome.Destinations, "book.nfo");
            AssertNoDirectoriesUnder(bookFolder);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    /// <summary>
    /// Compares the destinations as paths relative to the book folder, so a failure names the
    /// tree that was mirrored instead of two absolute paths that differ somewhere off the end
    /// of the line.
    /// </summary>
    private static void AssertPlacedAt(
        string bookFolder,
        IReadOnlyList<string> destinations,
        params string[] expectedRelativePaths)
    {
        Assert.Equal(
            expectedRelativePaths,
            destinations.Select(destination => Path.GetRelativePath(bookFolder, destination)));
    }

    /// <summary>
    /// The book folder gained no subdirectories. Asserting only the destination paths would not
    /// have caught the defect on its own, because a mirrored tree is still inside the book
    /// folder, and the directories outlive a companion that later fails to publish.
    /// </summary>
    private static void AssertNoDirectoriesUnder(string bookFolder)
    {
        Assert.Equal(
            [],
            Directory.GetDirectories(bookFolder, "*", SearchOption.AllDirectories)
                .Select(directory => Path.GetRelativePath(bookFolder, directory)));
    }

    private static void Cleanup(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
