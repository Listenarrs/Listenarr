using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs;

[Trait("Name", "MoveManifestIdentityTests")]
[Trait("Category", "Application")]
public sealed class MoveManifestIdentityTests : BaseTests
{
    [Fact]
    public void Version_IsOne()
    {
        Assert.Equal(1, MoveManifestIdentity.Version);
    }

    [Fact]
    public void SourceManifestsMatch_TargetBoundaryAuthorization_IsNotSourceContent()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var current = new[] { SourceFile("book.m4b", 1, 2, 'A') };
        var persisted = new List<MoveJobEntry>
        {
            PersistedFile("book.m4b", 1, 2, 'A'),
            MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                1,
                "target-generation")
        };

        Assert.True(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Fact]
    public void CreateDeduplicationKey_TargetBoundaryGenerationChangesIdentity()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var source = "/downloads/book";
        var target = "/library/book";
        var sourceIdentity = new PathIdentitySnapshot(
            semantics.Syntax,
            semantics.CaseSensitivity,
            FileSystemCaseSensitivityMode.Sensitive,
            "/downloads");
        var targetIdentity = new PathIdentitySnapshot(
            semantics.Syntax,
            semantics.CaseSensitivity,
            FileSystemCaseSensitivityMode.Sensitive,
            "/library");
        var firstEntries = new List<MoveJobEntry>
        {
            PersistedFile("book.m4b", 1, 2, 'A'),
            MoveManifestIdentity.CreateTargetBoundaryAuthorization(1, "generation-a")
        };
        var secondEntries = new List<MoveJobEntry>
        {
            PersistedFile("book.m4b", 1, 2, 'A'),
            MoveManifestIdentity.CreateTargetBoundaryAuthorization(1, "generation-b")
        };

        var first = MoveManifestIdentity.CreateDeduplicationKey(
            1,
            source,
            sourceIdentity,
            target,
            targetIdentity,
            firstEntries);
        var second = MoveManifestIdentity.CreateDeduplicationKey(
            1,
            source,
            sourceIdentity,
            target,
            targetIdentity,
            secondEntries);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SourceManifestsMatch_PipeAndNewlinePaths_DoNotCollide()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var embeddedRecord = new[]
        {
            new MoveSourceManifestEntry(
                "a|1|0|621355968000000000|\nb",
                MoveJobEntryType.Directory,
                0,
                DateTime.UnixEpoch,
                null)
        };
        var separateRecords = new[]
        {
            new MoveJobEntry
            {
                RelativePath = "a",
                EntryType = MoveJobEntryType.Directory
            },
            new MoveJobEntry
            {
                RelativePath = "b",
                EntryType = MoveJobEntryType.Directory
            }
        };

        Assert.False(MoveManifestIdentity.SourceManifestsMatch(
            embeddedRecord,
            separateRecords,
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_NormalizesInsensitivePathsAndIgnoresDirectoryTimestamp()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var current = new MoveSourceManifestEntry[]
        {
            new(
                "Disc/One",
                MoveJobEntryType.Directory,
                0,
                DateTime.UtcNow,
                null),
            new(
                "Disc/One/Book.m4b",
                MoveJobEntryType.File,
                5,
                DateTime.UnixEpoch,
                new string('a', 64))
        };
        var persisted = new MoveJobEntry[]
        {
            new()
            {
                RelativePath = "disc\\one",
                EntryType = MoveJobEntryType.Directory,
                LastWriteTimeUtc = DateTime.UnixEpoch.AddYears(10)
            },
            new()
            {
                RelativePath = "disc\\one\\book.m4b",
                EntryType = MoveJobEntryType.File,
                Length = 5,
                LastWriteTimeUtc = DateTime.UnixEpoch,
                Sha256 = new string('A', 64)
            }
        };

        Assert.True(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_FileTimestampChanges_ReturnsFalse()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var current = new[]
        {
            new MoveSourceManifestEntry(
                "book.m4b",
                MoveJobEntryType.File,
                5,
                DateTime.UnixEpoch,
                new string('A', 64))
        };
        var persisted = new[]
        {
            new MoveJobEntry
            {
                RelativePath = "book.m4b",
                EntryType = MoveJobEntryType.File,
                Length = 5,
                LastWriteTimeUtc = DateTime.UnixEpoch.AddTicks(1),
                Sha256 = new string('A', 64)
            }
        };

        Assert.False(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_DirectorySetChanges_ReturnsFalse()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var current = new[]
        {
            new MoveSourceManifestEntry(
                "CD1/book.m4b",
                MoveJobEntryType.File,
                5,
                DateTime.UnixEpoch,
                new string('A', 64))
        };
        var persisted = new[]
        {
            new MoveJobEntry
            {
                RelativePath = "CD1",
                EntryType = MoveJobEntryType.Directory
            },
            new MoveJobEntry
            {
                RelativePath = "CD1/book.m4b",
                EntryType = MoveJobEntryType.File,
                Length = 5,
                LastWriteTimeUtc = DateTime.UnixEpoch,
                Sha256 = new string('A', 64)
            }
        };

        Assert.False(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_EntryOrderingDoesNotAffectIdentity()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var current = new[]
        {
            SourceFile("b.m4b", length: 2, ticks: 2, hashCharacter: 'B'),
            SourceFile("a.m4b", length: 1, ticks: 1, hashCharacter: 'A')
        };
        var persisted = new[]
        {
            PersistedFile("a.m4b", length: 1, ticks: 1, hashCharacter: 'A'),
            PersistedFile("b.m4b", length: 2, ticks: 2, hashCharacter: 'B')
        };

        Assert.True(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("type")]
    [InlineData("length")]
    [InlineData("timestamp")]
    [InlineData("hash")]
    public void SourceManifestsMatch_EveryPersistedFieldAffectsIdentity(string changedField)
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var current = new[] { SourceFile("book.m4b", 10, 20, 'A') };
        var persisted = PersistedFile("book.m4b", 10, 20, 'A');
        switch (changedField)
        {
            case "path":
                persisted.RelativePath = "other.m4b";
                break;
            case "type":
                persisted.EntryType = MoveJobEntryType.Directory;
                break;
            case "length":
                persisted.Length++;
                break;
            case "timestamp":
                persisted.LastWriteTimeUtc = persisted.LastWriteTimeUtc.AddTicks(1);
                break;
            case "hash":
                persisted.Sha256 = new string('B', 64);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedField));
        }

        Assert.False(MoveManifestIdentity.SourceManifestsMatch(
            current,
            [persisted],
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_UnicodeNfcAndWindowsSeparatorsRemainStable()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var current = new[]
        {
            SourceFile("Cafe\u0301/Part|One\nBook.m4b", 1, 2, 'A')
        };
        var persisted = new[]
        {
            PersistedFile("CAFÉ\\PART|ONE\nBOOK.M4B", 1, 2, 'A')
        };

        Assert.True(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_EmptyAndVeryLongUnusualPathsAreUnambiguous()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var unusualPath = new string('x', 220) + "|chapter\n01.m4b";
        var unusual = new[] { SourceFile(unusualPath, 1, 2, 'A') };

        Assert.True(MoveManifestIdentity.SourceManifestsMatch(
            unusual,
            [PersistedFile(unusualPath, 1, 2, 'A')],
            semantics));
        Assert.False(MoveManifestIdentity.SourceManifestsMatch(
            [],
            [PersistedFile(unusualPath, 1, 2, 'A')],
            semantics));
    }

    private static MoveSourceManifestEntry SourceFile(
        string path,
        long length,
        long ticks,
        char hashCharacter) =>
        new(
            path,
            MoveJobEntryType.File,
            length,
            DateTime.UnixEpoch.AddTicks(ticks),
            new string(hashCharacter, 64));

    private static MoveJobEntry PersistedFile(
        string path,
        long length,
        long ticks,
        char hashCharacter) =>
        new()
        {
            RelativePath = path,
            EntryType = MoveJobEntryType.File,
            Length = length,
            LastWriteTimeUtc = DateTime.UnixEpoch.AddTicks(ticks),
            Sha256 = new string(hashCharacter, 64)
        };
}
