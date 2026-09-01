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
using Listenarr.Application.Common.Contracts;
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

/// <summary>
/// A source reached through a symlinked directory is refused, and the refusal says so.
///
/// Refusing a linked ancestor is deliberate and already covered by
/// CheckPublicationSource_LinkedAncestor_ReturnsUnsupported. What was missing is any way for an
/// operator to know that is what happened: the underlying failure is an ENOTDIR from openat with
/// O_NOFOLLOW, reported as one fixed sentence about durable physical generations.
/// </summary>
[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverSymlinkedSourcePathTests")]
[Trait("Category", "PublicationCapability")]
public sealed class FileMoverSymlinkedSourcePathTests : BaseTests
{
    private (string Direct, string ViaSymlink) CreateSymlinkedLayout(string name)
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

        return (file, Path.Join(link, "completed", "book.m4b"));
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

        // But the reason must now be actionable rather than a fixed sentence about generations.
        Assert.Contains("symbolic link", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("configure the real path", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
