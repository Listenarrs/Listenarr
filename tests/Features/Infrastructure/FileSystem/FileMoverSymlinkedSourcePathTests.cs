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
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

/// <summary>
/// A source reached through a symlinked directory is refused, and the refusal says so.
///
/// Refusing a linked ancestor is deliberate and already covered by
/// CheckPublicationSource_LinkedAncestor_ReturnsUnsupported. What was missing is any way for an
/// operator to know that is what happened: the underlying failure is an ENOTDIR from openat with
/// O_NOFOLLOW, reported as one fixed sentence that names no cause.
/// </summary>
[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverSymlinkedSourcePathTests")]
[Trait("Category", "PublicationCapability")]
public sealed class FileMoverSymlinkedSourcePathTests : BaseTests
{
    private (string Direct, string ViaSymlink, string Link) CreateSymlinkedLayout(string name)
    {
        var root = FileService.GetTempDirectory(name);
        var real = Path.Join(root, "real-downloads", "completed");
        Directory.CreateDirectory(real);
        var file = Path.Join(real, "book.m4b");
        File.WriteAllText(file, "audio");

        // The shape this reproduces: one component of the path is a symlink to the real
        // directory, which is what a cache tier or a pooled mount usually looks like.
        var link = Path.Join(root, "downloads");
        Directory.CreateSymbolicLink(link, Path.Join(root, "real-downloads"));

        return (file, Path.Join(link, "completed", "book.m4b"), link);
    }

    [DirectoryLinkFact]
    [Trait("Scenario", "The same file is publishable by its real path")]
    public async Task CheckAsync_RealPath_IsSupported()
    {
        // The control. Both paths name the same file on the same filesystem, so anything that
        // fails for one and not the other is about the path, not the file.
        var layout = CreateSymlinkedLayout("symlink-source-control");
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(
            _provider.GetRequiredService<IFileMover>());

        var result = await capability.CheckAsync(layout.Direct);

        Assert.True(result.IsSupported, result.Reason);
    }

    [DirectoryLinkFact]
    [Trait("Scenario", "The refusal names the symlinked directory that caused it")]
    public async Task CheckAsync_PathThroughSymlinkedDirectory_NamesTheLink()
    {
        var layout = CreateSymlinkedLayout("symlink-source-refused");
        Assert.True(File.Exists(layout.ViaSymlink), "the file must be reachable through the link");

        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(
            _provider.GetRequiredService<IFileMover>());

        var result = await capability.CheckAsync(layout.ViaSymlink);

        // Still refused. That is the intended boundary and this test does not argue with it.
        Assert.False(result.IsSupported);

        // But the reason must now be actionable rather than a fixed sentence that names no cause.
        Assert.Contains("symbolic link", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("configure the real path", result.Reason, StringComparison.OrdinalIgnoreCase);

        // Both phrases above are literals in the format string, so they survive a build that
        // captures no cause at all. These two do not: the linked segment comes from the walk and
        // the type name comes from the caught exception, so an empty cause fails here.
        Assert.Contains(Path.GetFileName(layout.Link), result.Reason, StringComparison.Ordinal);
        Assert.Matches(@"\w+Exception: \S", result.Reason!);
    }

    /// <summary>
    /// A configured root that is itself reached through a symlink, with a source file beneath it.
    /// </summary>
    /// <remarks>
    /// The mover is built with the root folder repository so that CheckAsync takes the managed
    /// path, where the configured root is opened through the boundary authorizer and its link is
    /// followed. Optionally adds a nested root at the book directory whose identity is unavailable,
    /// which refuses the source for a reason that has nothing to do with the link.
    /// </remarks>
    private async Task<(FileMover Mover, string Source)> CreateLinkedManagedRootAsync(
        string name,
        bool addUnavailableNestedRoot)
    {
        var directory = FileService.GetTempDirectory(name);
        var physicalRoot = Directory.CreateDirectory(Path.Join(directory, "physical-library")).FullName;
        var linkedRoot = Path.Join(directory, "library");
        Directory.CreateSymbolicLink(linkedRoot, physicalRoot);
        await AddAuthorizedRootAsync(linkedRoot, "Linked library root");

        var bookDirectory = Directory.CreateDirectory(Path.Join(linkedRoot, "Book")).FullName;
        var source = Path.Join(bookDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");

        if (addUnavailableNestedRoot)
        {
            await AddUnavailableRootAsync(bookDirectory, "Unavailable nested root");
        }

        return (CreateManagedMover(), source);
    }

    private FileMover CreateManagedMover()
    {
        return new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            rootFolderRepository: _rootFolderRepository);
    }

    private async Task AddUnavailableRootAsync(string path, string name)
    {
        var dbContextFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.RootFolders.Add(new RootFolder
        {
            Name = name,
            Path = path,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            PathIdentityState = PathIdentityState.Unavailable,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
        });
        await db.SaveChangesAsync();
    }

    [DirectoryLinkFact]
    [Trait("Scenario", "A configured root reached through a symlink is publishable")]
    public async Task CheckAsync_ManagedRootReachedThroughSymlink_IsSupported()
    {
        // The control. On the managed path the configured root is opened by following its link,
        // so the link alone is not a reason to refuse.
        var (mover, source) = await CreateLinkedManagedRootAsync(
            "symlink-managed-root-control",
            addUnavailableNestedRoot: false);

        var result = await mover.CheckAsync(source);

        Assert.True(result.IsSupported, result.Reason);
    }

    [DirectoryLinkFact]
    [Trait("Scenario", "An unrelated refusal beneath a linked configured root does not blame the link")]
    public async Task CheckAsync_ManagedRootReachedThroughSymlink_UnrelatedFailure_DoesNotBlameTheLink()
    {
        var (mover, source) = await CreateLinkedManagedRootAsync(
            "symlink-managed-root-unrelated-failure",
            addUnavailableNestedRoot: true);

        var result = await mover.CheckAsync(source);

        Assert.False(result.IsSupported);
        Assert.StartsWith("InvalidOperationException: ", result.Reason, StringComparison.Ordinal);

        // The link above is supported, as the control shows, so advice to configure the real
        // path instead would send the operator after the wrong thing.
        Assert.DoesNotContain("symbolic link", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configure the real path", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [DirectoryLinkFact]
    [Trait("Scenario", "A refusal that is not the overlap, beneath a linked configured root, does not blame the link")]
    public async Task CheckAsync_ManagedRootReachedThroughSymlink_RootCannotBeAuthorized_DoesNotBlameTheLink()
    {
        // The unrelated failure above is the overlap, which is refused before the walk would look
        // at a single directory. Here the root resolves and is then refused because nothing can
        // authorize it, so only stopping the walk at the root keeps the root's link out of it.
        var (_, source) = await CreateLinkedManagedRootAsync(
            "symlink-managed-root-unauthorized",
            addUnavailableNestedRoot: false);
        var moverWithoutAuthorizer = new FileMover(
            NullLogger<FileMover>.Instance,
            rootFolderRepository: _rootFolderRepository);

        var result = await moverWithoutAuthorizer.CheckAsync(source);

        Assert.False(result.IsSupported);
        Assert.StartsWith("InvalidOperationException: ", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved managed boundary", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("symbolic link", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configure the real path", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [DirectoryLinkFact]
    [Trait("Scenario", "A source covered only by an unavailable root reached through a symlink does not blame the link")]
    public async Task CheckAsync_OnlyAnUnavailableRootCoversALinkedSource_DoesNotBlameTheLink()
    {
        // With no root it can resolve, the walk would otherwise cover the whole path and find the
        // root's link. The overlap is refused before any directory is opened, so the link cannot
        // be what caused it.
        var directory = FileService.GetTempDirectory("symlink-unavailable-root-only");
        var physicalRoot = Directory.CreateDirectory(Path.Join(directory, "physical-library")).FullName;
        var linkedRoot = Path.Join(directory, "library");
        Directory.CreateSymbolicLink(linkedRoot, physicalRoot);
        await AddUnavailableRootAsync(linkedRoot, "Unavailable linked root");
        var bookDirectory = Directory.CreateDirectory(Path.Join(linkedRoot, "Book")).FullName;
        var source = Path.Join(bookDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");

        var result = await CreateManagedMover().CheckAsync(source);

        Assert.False(result.IsSupported);
        Assert.StartsWith("InvalidOperationException: ", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("symbolic link", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configure the real path", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [DirectoryLinkFact]
    [Trait("Scenario", "A link below a configured root is still named")]
    public async Task CheckAsync_LinkBelowAConfiguredRoot_NamesTheLink()
    {
        // Stopping the walk at the root must not silence it below the root, where segments are
        // still opened without following links and a link is still the cause.
        var directory = FileService.GetTempDirectory("symlink-below-configured-root");
        var root = Directory.CreateDirectory(Path.Join(directory, "library")).FullName;
        await AddAuthorizedRootAsync(root, "Library root");
        var elsewhere = Directory.CreateDirectory(Path.Join(directory, "elsewhere")).FullName;
        var link = Path.Join(root, "LinkedSub");
        Directory.CreateSymbolicLink(link, elsewhere);
        var bookDirectory = Directory.CreateDirectory(Path.Join(link, "Book")).FullName;
        var source = Path.Join(bookDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");

        var result = await CreateManagedMover().CheckAsync(source);

        Assert.False(result.IsSupported);
        Assert.Matches(@"^\w+Exception: \S", result.Reason!);
        Assert.Contains("symbolic link", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LinkedSub", result.Reason, StringComparison.Ordinal);
    }
}
