using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Architecture;

/// <summary>
/// The guard that fails a build when production code derives a release blocklist key for itself
/// is only worth having if it can fail, so each rule it enforces is pinned here against a sample
/// of the mistake and against the ordinary code it must not accuse.
/// </summary>
[Trait("Name", "ReleaseIdentitySourceAnalyzerTests")]
[Trait("Category", "Architecture")]
public sealed class ReleaseIdentitySourceAnalyzerTests : BaseTests
{
    [Fact]
    [Trait("Scenario", "A second site builds a key out of release fields")]
    public void Analyze_KeyComposedFromReleaseFields_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                public async Task Block(Download download)
                {
                    var releaseIdentifier = $"{download.Title}|{download.TotalSize}";
                    await blocklistService.BlockAsync(1, releaseIdentifier, "t", null, "failed");
                }
            }
            """;

        var violation = Assert.Single(ReleaseIdentitySourceAnalyzer.Analyze(source));
        Assert.Equal(5, violation.Line);
        Assert.Contains("combines 2 release fields", violation.Reason);
        Assert.Contains("releaseIdentifier", violation.Reason);
        Assert.Contains("ReleaseIdentity", violation.Reason);
    }

    [Fact]
    [Trait("Scenario", "The same mistake written with concatenation")]
    public void Analyze_KeyConcatenatedFromReleaseFields_IsReportedOnce()
    {
        const string source = """
            public sealed class Example
            {
                private static string IdentityFor(SearchResult result)
                {
                    return result.Title + "|" + result.Size + "|" + result.NzbUrl;
                }
            }
            """;

        var violation = Assert.Single(ReleaseIdentitySourceAnalyzer.Analyze(source));
        Assert.Equal(5, violation.Line);
        Assert.Contains("combines 3 release fields", violation.Reason);
        Assert.Contains("IdentityFor", violation.Reason);
    }

    [Fact]
    [Trait("Scenario", "Hashing release fields into a key")]
    public void Analyze_ReleaseFieldsHashedIntoAnIdentity_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                public void Block(Download download)
                {
                    var identity = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(download.OriginalUrl)));
                }
            }
            """;

        var violation = Assert.Single(ReleaseIdentitySourceAnalyzer.Analyze(source));
        Assert.Equal(5, violation.Line);
        Assert.Contains("hashes release fields", violation.Reason);
    }

    [Fact]
    [Trait("Scenario", "Reproducing the key format")]
    public void Analyze_KeySchemePrefixWrittenOutsideTheOwner_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                public string Key(string hash) => "btih:" + hash.ToLowerInvariant();
            }
            """;

        var violation = Assert.Single(ReleaseIdentitySourceAnalyzer.Analyze(source));
        Assert.Equal(3, violation.Line);
        Assert.Contains("blocklist key prefix", violation.Reason);
    }

    [Fact]
    [Trait("Scenario", "Naming the metadata slot twice")]
    public void Analyze_MetadataSlotSpelledAsALiteral_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                public string? Stamped(Download download) =>
                    download.GetMetadataString("ReleaseIdentity");
            }
            """;

        var violation = Assert.Single(ReleaseIdentitySourceAnalyzer.Analyze(source));
        Assert.Equal(4, violation.Line);
        Assert.Contains("MetadataKey", violation.Reason);
    }

    [Fact]
    [Trait("Scenario", "The supported way of getting a key")]
    public void Analyze_CallingReleaseIdentity_IsNotReported()
    {
        const string source = """
            public sealed class Example
            {
                public async Task Block(Download download)
                {
                    var identifier = ReleaseIdentity.ForGrabbed(download);
                    await blocklistService.BlockAsync(
                        1, identifier, download.Title, download.ExpectedFileSize, "failed");
                }
            }
            """;

        Assert.Empty(ReleaseIdentitySourceAnalyzer.Analyze(source));
    }

    [Fact]
    [Trait("Scenario", "Ordinary text that happens to mention a title and a size")]
    public void Analyze_ReleaseFieldsInAMessage_IsNotReported()
    {
        // A guard that accuses log lines and exception messages gets deleted, and a deleted guard
        // is worse than no guard. A composition only counts once it reaches an identity.
        const string source = """
            public sealed class Example
            {
                public void Log(SearchResult result)
                {
                    var message = $"Rejected {result.Title} ({result.Size} bytes)";
                    throw new InvalidOperationException(result.Title + " at " + result.Size);
                }
            }
            """;

        Assert.Empty(ReleaseIdentitySourceAnalyzer.Analyze(source));
    }

    [Fact]
    [Trait("Scenario", "A magnet URI is not a blocklist key")]
    public void Analyze_InfoHashInsideAMagnetUri_IsNotReported()
    {
        const string source = """
            public sealed class Example
            {
                public string Magnet(string hash, string title) =>
                    $"magnet:?xt=urn:btih:{hash}&dn={title}";
            }
            """;

        Assert.Empty(ReleaseIdentitySourceAnalyzer.Analyze(source));
    }

    [Fact]
    [Trait("Scenario", "One release field is not an identity")]
    public void Analyze_SingleReleaseFieldStoredAsAnIdentifier_IsNotReported()
    {
        const string source = """
            public sealed class Example
            {
                public string Identifier(SearchResult result) => result.Title.Trim() + " ";
            }
            """;

        Assert.Empty(ReleaseIdentitySourceAnalyzer.Analyze(source));
    }
}
