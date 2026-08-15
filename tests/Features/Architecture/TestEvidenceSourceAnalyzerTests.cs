using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Architecture;

[Trait("Name", "TestEvidenceSourceAnalyzerTests")]
[Trait("Category", "Architecture")]
public sealed class TestEvidenceSourceAnalyzerTests : BaseTests
{
    [Fact]
    public void Analyze_RuntimeInformationGuardReturn_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                [Fact]
                public void Test()
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        return;
                    }
                    Assert.True(true);
                }
            }
            """;

        var violation = Assert.Single(TestEvidenceSourceAnalyzer.Analyze(source));
        Assert.Contains("operating-system guard", violation.Reason);
    }

    [Fact]
    public void Analyze_CatchAndReturn_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                [Fact]
                public void Test()
                {
                    try
                    {
                        Directory.CreateSymbolicLink("link", "target");
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    Assert.True(true);
                }
            }
            """;

        var violation = Assert.Single(TestEvidenceSourceAnalyzer.Analyze(source));
        Assert.Contains("catches", violation.Reason);
    }

    [Fact]
    public void Analyze_CapabilityProbeReturn_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                [Fact]
                public void Test()
                {
                    if (!TryCreateLink())
                    {
                        return;
                    }
                    Assert.True(true);
                }
            }
            """;

        var violation = Assert.Single(TestEvidenceSourceAnalyzer.Analyze(source));
        Assert.Contains("capability probe", violation.Reason);
    }

    [Fact]
    public void Analyze_ReturnInsideLambda_DoesNotExitTest()
    {
        const string source = """
            public sealed class Example
            {
                [Fact]
                public void Test()
                {
                    Action callback = () =>
                    {
                        return;
                    };
                    callback();
                    Assert.True(true);
                }
            }
            """;

        Assert.Empty(TestEvidenceSourceAnalyzer.Analyze(source));
    }

    [Fact]
    public void Analyze_PlatformFactWithoutEarlyReturn_IsAccepted()
    {
        const string source = """
            public sealed class Example
            {
                [LinuxFact]
                public void Test()
                {
                    Assert.True(OperatingSystem.IsLinux());
                }
            }
            """;

        Assert.Empty(TestEvidenceSourceAnalyzer.Analyze(source));
    }

    [Theory]
    [InlineData("DirectoryLinkFact")]
    [InlineData("DirectoryLinkFactAttribute")]
    [InlineData("FileLinkFact")]
    [InlineData("FileLinkFactAttribute")]
    [InlineData("DirectoryLinkTheory")]
    [InlineData("DirectoryLinkTheoryAttribute")]
    [InlineData("FileLinkTheory")]
    [InlineData("FileLinkTheoryAttribute")]
    [InlineData("FutureCapabilityFact")]
    [InlineData("FutureCapabilityTheoryAttribute")]
    public void Analyze_CustomTestAttributeReturn_IsReported(string attributeName)
    {
        var source = $$"""
            public sealed class Example
            {
                [{{attributeName}}]
                public void Test()
                {
                    if (!TryCreateLink())
                    {
                        return;
                    }
                    Assert.True(true);
                }
            }
            """;

        var violation = Assert.Single(TestEvidenceSourceAnalyzer.Analyze(source));
        Assert.Contains("capability probe", violation.Reason);
    }
}
