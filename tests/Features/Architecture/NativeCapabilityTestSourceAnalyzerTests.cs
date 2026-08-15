using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Architecture;

[Trait("Name", "NativeCapabilityTestSourceAnalyzerTests")]
[Trait("Category", "Architecture")]
public sealed class NativeCapabilityTestSourceAnalyzerTests : BaseTests
{
    [Fact]
    public void Analyze_DirectoryLinkUnderPlainLinuxFact_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                [LinuxFact]
                public void Test()
                {
                    Directory.CreateSymbolicLink("link", "target");
                }
            }
            """;

        var violation = Assert.Single(
            NativeCapabilityTestSourceAnalyzer.Analyze(source));

        Assert.Equal("Test", violation.MethodName);
        Assert.Equal("directory symbolic links", violation.MissingCapability);
    }

    [Fact]
    public void Analyze_TransitiveFileLinkUnderPlainFact_IsReported()
    {
        const string source = """
            public sealed class Example
            {
                [Fact]
                public void Test()
                {
                    CreateLink();
                }

                private static void CreateLink()
                {
                    File.CreateSymbolicLink("link", "target");
                }
            }
            """;

        var violation = Assert.Single(
            NativeCapabilityTestSourceAnalyzer.Analyze(source));

        Assert.Equal("file symbolic links", violation.MissingCapability);
    }

    [Theory]
    [InlineData("DirectoryLinkFact")]
    [InlineData("DirectoryLinkTheory")]
    [InlineData("LinuxDirectoryAndFileLinkFact")]
    public void Analyze_DirectoryCapabilityAttribute_IsAccepted(string attribute)
    {
        var source = $$"""
            public sealed class Example
            {
                [{{attribute}}]
                public void Test()
                {
                    Directory.CreateSymbolicLink("link", "target");
                }
            }
            """;

        Assert.Empty(NativeCapabilityTestSourceAnalyzer.Analyze(source));
    }

    [Theory]
    [InlineData("FileLinkFact")]
    [InlineData("FileLinkTheory")]
    [InlineData("LinuxDirectoryAndFileLinkFact")]
    public void Analyze_FileCapabilityAttribute_IsAccepted(string attribute)
    {
        var source = $$"""
            public sealed class Example
            {
                [{{attribute}}]
                public void Test()
                {
                    File.CreateSymbolicLink("link", "target");
                }
            }
            """;

        Assert.Empty(NativeCapabilityTestSourceAnalyzer.Analyze(source));
    }

    [Fact]
    public void AnalyzeSources_HelperInAnotherPartialFile_IsTraced()
    {
        var sources = new[]
        {
            new NativeCapabilityTestSourceAnalyzer.NativeCapabilitySource(
                "Example.Tests.cs",
                """
                public sealed partial class Example
                {
                    [Fact]
                    public void Test()
                    {
                        CreateLink();
                    }
                }
                """),
            new NativeCapabilityTestSourceAnalyzer.NativeCapabilitySource(
                "Example.Helpers.cs",
                """
                public sealed partial class Example
                {
                    private static void CreateLink()
                    {
                        Directory.CreateSymbolicLink("link", "target");
                    }
                }
                """)
        };

        var violation = Assert.Single(
            NativeCapabilityTestSourceAnalyzer.AnalyzeSources(sources));

        Assert.Equal("Example.Tests.cs", violation.SourcePath);
        Assert.Equal("directory symbolic links", violation.MissingCapability);
    }

    [Fact]
    public void AnalyzeSources_QualifiedStaticHelper_IsTraced()
    {
        var sources = new[]
        {
            new NativeCapabilityTestSourceAnalyzer.NativeCapabilitySource(
                "Example.Tests.cs",
                """
                public sealed class Example
                {
                    [Fact]
                    public void Test()
                    {
                        LinkHelper.CreateLink();
                    }
                }
                """),
            new NativeCapabilityTestSourceAnalyzer.NativeCapabilitySource(
                "LinkHelper.cs",
                """
                public static class LinkHelper
                {
                    public static void CreateLink()
                    {
                        File.CreateSymbolicLink("link", "target");
                    }
                }
                """)
        };

        var violation = Assert.Single(
            NativeCapabilityTestSourceAnalyzer.AnalyzeSources(sources));

        Assert.Equal("Example.Tests.cs", violation.SourcePath);
        Assert.Equal("file symbolic links", violation.MissingCapability);
    }

    [Fact]
    public void Analyze_CapabilityPolicyDeclaration_IsNotTreatedAsLinkCreation()
    {
        const string source = """
            public sealed class Example
            {
                [Fact]
                public void Test()
                {
                    NativeTestCapabilityPolicy.RequireAvailable(
                        NativeTestCapability.DirectorySymbolicLinks);
                }
            }
            """;

        Assert.Empty(NativeCapabilityTestSourceAnalyzer.Analyze(source));
    }

    [Fact]
    public void Analyze_ExternalMethodWithSameName_IsNotTreatedAsLocalHelper()
    {
        const string source = """
            public sealed class Example
            {
                [Fact]
                public void Test()
                {
                    external.CreateLink();
                }

                private static void CreateLink()
                {
                    Directory.CreateSymbolicLink("link", "target");
                }
            }
            """;

        Assert.Empty(NativeCapabilityTestSourceAnalyzer.Analyze(source));
    }

    [Fact]
    public void Analyze_BothLinkTypes_RequiresBothCapabilities()
    {
        const string source = """
            public sealed class Example
            {
                [DirectoryLinkFact]
                public void Test()
                {
                    Directory.CreateSymbolicLink("directory-link", "directory-target");
                    File.CreateSymbolicLink("file-link", "file-target");
                }
            }
            """;

        var violation = Assert.Single(
            NativeCapabilityTestSourceAnalyzer.Analyze(source));

        Assert.Equal("file symbolic links", violation.MissingCapability);
    }
}
