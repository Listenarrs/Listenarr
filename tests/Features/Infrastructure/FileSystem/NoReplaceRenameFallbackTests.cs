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
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "NoReplaceRenameFallbackTests")]
[Trait("Category", "Infrastructure")]
public sealed class NoReplaceRenameFallbackTests : BaseTests
{
    private const int EPERM = 1;
    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int EEXIST = 17;
    private const int EXDEV = 18;
    private const int EINVAL = 22;
    private const int ENOSYS = 38;
    private const int EOPNOTSUPP = 95;

    private sealed class Script
    {
        public readonly List<string> Calls = new();
        public int RenameErrno;
        public int LinkErrno;
        public int UnlinkSourceErrno;

        public PinnedDirectoryCreation.NoReplaceRenamePrimitives Primitives => new(
            (sfd, s, dfd, d) =>
            {
                Calls.Add($"rename2 {sfd}/{s} -> {dfd}/{d}");
                return RenameErrno == 0 ? (0, 0) : (-1, RenameErrno);
            },
            (sfd, s, dfd, d) =>
            {
                Calls.Add($"link {sfd}/{s} -> {dfd}/{d}");
                return LinkErrno == 0 ? (0, 0) : (-1, LinkErrno);
            },
            (fd, name) =>
            {
                Calls.Add($"unlink {fd}/{name}");
                if (fd == 10 && UnlinkSourceErrno != 0) return (-1, UnlinkSourceErrno);
                return (0, 0);
            });
    }

    private static int Run(Script script) =>
        PinnedDirectoryCreation.RenameNoReplaceLinuxCore(10, "a.m4b", 20, "b.m4b", script.Primitives);

    [Fact]
    public void RenameSupported_UsesRenameOnly()
    {
        var script = new Script();
        Assert.Equal(0, Run(script));
        Assert.Equal(new[] { "rename2 10/a.m4b -> 20/b.m4b" }, script.Calls);
    }

    [Fact]
    public void RenameRefusedBecauseDestinationExists_ReportsEexistWithoutFallback()
    {
        var script = new Script { RenameErrno = EEXIST };
        Assert.Equal(EEXIST, Run(script));
        Assert.Single(script.Calls);
    }

    [Theory]
    [InlineData(EINVAL)]
    [InlineData(ENOSYS)]
    [InlineData(EOPNOTSUPP)]
    public void RenameUnsupported_FallsBackToLinkThenUnlinkSource(int unsupported)
    {
        // NFS (EINVAL), old kernels (ENOSYS), some FUSE stacks (EOPNOTSUPP):
        // link the inode under the new name, then drop the old name.
        var script = new Script { RenameErrno = unsupported };
        Assert.Equal(0, Run(script));
        Assert.Equal(
            new[] { "rename2 10/a.m4b -> 20/b.m4b", "link 10/a.m4b -> 20/b.m4b", "unlink 10/a.m4b" },
            script.Calls);
    }

    [Fact]
    public void RenameUnsupported_DestinationExists_LinkRefusesAndNothingIsUnlinked()
    {
        // The no-replace contract survives the fallback: linkat refuses to
        // overwrite, and the source is left exactly where it was.
        var script = new Script { RenameErrno = EINVAL, LinkErrno = EEXIST };
        Assert.Equal(EEXIST, Run(script));
        Assert.DoesNotContain(script.Calls, c => c.StartsWith("unlink"));
    }

    [Theory]
    [InlineData(EPERM)]
    [InlineData(EXDEV)]
    [InlineData(EOPNOTSUPP)]
    public void RenameUnsupported_HardLinksUnsupportedToo_ReportsOriginalUnsupportedErrno(int linkErrno)
    {
        // Callers classify 22/38/95 as "native rename unsupported" and switch to
        // their verified-copy fallback — that classification must still fire.
        var script = new Script { RenameErrno = EINVAL, LinkErrno = linkErrno };
        Assert.Equal(EINVAL, Run(script));
        Assert.DoesNotContain(script.Calls, c => c.StartsWith("unlink"));
    }

    [Fact]
    public void RenameUnsupported_SourceUnlinkFails_RollsBackTheNewLink()
    {
        var script = new Script { RenameErrno = EINVAL, UnlinkSourceErrno = EACCES };
        Assert.Equal(EACCES, Run(script));
        Assert.Equal("unlink 10/a.m4b", script.Calls[2]);
        Assert.Equal("unlink 20/b.m4b", script.Calls[3]);
    }

    [Fact]
    public void RenameUnsupported_SourceAlreadyGoneAfterLink_IsSuccess()
    {
        // The inode now lives at the destination; a vanished source name is a
        // completed move, not a failure.
        var script = new Script { RenameErrno = EINVAL, UnlinkSourceErrno = ENOENT };
        Assert.Equal(0, Run(script));
        Assert.Equal(3, script.Calls.Count);
    }

    [Theory]
    [InlineData(EINVAL, true)]
    [InlineData(ENOSYS, true)]
    [InlineData(EOPNOTSUPP, true)]
    [InlineData(EEXIST, false)]
    [InlineData(EXDEV, false)]
    [InlineData(EACCES, false)]
    public void IsNoReplaceRenameUnsupportedError_ClassifiesErrno(int errno, bool unsupported)
    {
        Assert.Equal(unsupported, PinnedDirectoryCreation.IsNoReplaceRenameUnsupportedError(errno));
    }
}
