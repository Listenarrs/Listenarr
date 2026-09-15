using System.Text.RegularExpressions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Downloads;

// A release identity is PERSISTED. It is written into the blocklist when a release fails and
// looked up by string equality on every later grab, so the wire format is a storage contract and
// not an implementation detail. Change the hash, the truncation, the case or the separator and
// every row already in the database becomes unreachable: the blocklist silently stops blocking,
// and the release that failed is grabbed again forever.
//
// That is not hypothetical. The blocklist has had four defects and all four were one defect, an
// identity derived one way in one place and another way somewhere else. The fix for that was to
// give one class sole ownership of the format. This file guards the other half: what that one
// owner is allowed to change about the format without anybody noticing.
//
// The existing ReleaseIdentityTests assert relational properties: same release gives the same
// key, different size gives a different one, the prefix is what you expect. All of those still
// pass if SHA256 becomes SHA1, if the digest stops being truncated to 32 characters, or if the
// hex is uppercased. These vectors are the statement those tests cannot make.
[Trait("Name", "ReleaseIdentityGoldenVectorTests")]
[Trait("Category", "Domain")]
public sealed class ReleaseIdentityGoldenVectorTests : BaseTests
{
    // Each vector pins one branch of ReleaseIdentity.For to an exact output. Computed
    // independently of the implementation rather than captured from it: SHA256 of the normalized
    // input, hex, lowercased, first 32 characters.
    public static TheoryData<string, string?, string?, string?, long?, string> Vectors() => new()
    {
        {
            "btih, explicit info-hash, trimmed and folded to lower case",
            "ABCDEF0123456789ABCDEF0123456789ABCDEF01", null, null, null,
            "btih:abcdef0123456789abcdef0123456789abcdef01"
        },
        {
            "name, title and size, the Usenet case that must survive a re-grab",
            null, null, "Some Book Title", 123456789L,
            "name:398a59f279088f1c1df8838f45d3c607"
        },
        {
            "name, title with no size, so the size slot is a literal question mark",
            null, null, "Some Book Title", null,
            "name:76d0b643f32c186b0d26e176fe39f86b"
        },
        {
            "name, title normalized for case and runs of whitespace before hashing",
            null, null, "  Some   BOOK   Title  ", 123456789L,
            "name:398a59f279088f1c1df8838f45d3c607"
        },
        {
            "url, last resort when there is no usable title",
            null, "https://indexer.example/get/abc?token=XYZ", null, null,
            "url:09fbce0a2ba90dec669e4583af7d76ec"
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void For_ProducesTheExactPersistedKey(
        string because, string? torrentInfoHash, string? releaseUrl, string? title, long? size, string expected)
    {
        var actual = ReleaseIdentity.For(torrentInfoHash, releaseUrl, title, size);

        Assert.Equal(expected, actual);
        Assert.False(string.IsNullOrWhiteSpace(because));
    }

    [Fact]
    public void TorrentHashFrom_MagnetLink_FoldsToTheSameKeyAsTheExplicitHash()
    {
        const string magnet = "magnet:?xt=urn:btih:DEADBEEF00112233445566778899AABBCCDDEEFF&dn=x";

        var viaMagnet = ReleaseIdentity.For(ReleaseIdentity.TorrentHashFrom(magnet), null, null, null);

        Assert.Equal("btih:deadbeef00112233445566778899aabbccddeeff", viaMagnet);
    }

    // The digest convention itself, stated once so a change to it fails here rather than in a
    // vector and leaves the reader guessing which part moved.
    [Theory]
    [InlineData("name:398a59f279088f1c1df8838f45d3c607")]
    [InlineData("url:09fbce0a2ba90dec669e4583af7d76ec")]
    public void HashedKeys_AreThirtyTwoCharactersOfLowercaseHex(string key)
    {
        var digest = key[(key.IndexOf(':') + 1)..];

        Assert.Equal(32, digest.Length);
        Assert.Matches("^[0-9a-f]{32}$", digest);
    }

    // The part the vectors above cannot cover on their own. A new protocol branch that returns a
    // key under a prefix nobody pinned would ship with no vector and nothing would notice, which
    // is the specific hole this file was asked to close. So read the source, collect every scheme
    // prefix it can emit, and require that each one is represented above.
    //
    // Reading the source rather than reflecting over the type because the prefixes are string
    // literals inside one method and there is nothing to reflect on.
    [Fact]
    public void EveryPrefixTheSourceCanEmit_HasAGoldenVector()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepositoryRoot(), "listenarr.domain", "Downloads", "ReleaseIdentity.cs"));

        // Matches `return "btih:" + ...` and `return "name:" + ...`, the shape every branch uses.
        var emitted = Regex.Matches(source, @"return\s+""(?<prefix>[a-z]+):""")
            .Select(match => match.Groups["prefix"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToList();

        var covered = Vectors()
            .Select(row => (string)row[5]!)
            .Select(key => key[..key.IndexOf(':')])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(emitted);
        Assert.Equal(emitted, covered);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "listenarr.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root from the test output directory.");
    }
}
