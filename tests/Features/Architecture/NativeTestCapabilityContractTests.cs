using System.Text.RegularExpressions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Architecture;

[Trait("Name", "NativeTestCapabilityContractTests")]
[Trait("Category", "Architecture")]
public sealed class NativeTestCapabilityContractTests : BaseTests
{
    [Fact]
    public void RequiredNativeTestCapabilities_AreAvailable()
    {
        var failures = NativeTestCapabilityPolicy.ProbeRequiredCapabilities();

        Assert.True(
            failures.Count == 0,
            "Required native test capabilities are unavailable. "
            + NativeTestCapabilityPolicy.DescribeHost()
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                failures.Select(failure =>
                    $"- {failure.Capability}: {failure.FailureReason}")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseRequiredCapabilities_EmptyValue_ReturnsEmpty(string? value)
    {
        var capabilities = NativeTestCapabilityPolicy.ParseRequiredCapabilities(value);

        Assert.Empty(capabilities);
    }

    [Fact]
    public void ParseRequiredCapabilities_ValidList_ParsesIndependentCapabilities()
    {
        var capabilities = NativeTestCapabilityPolicy.ParseRequiredCapabilities(
            " filesymboliclinks , DirectorySymbolicLinks ");

        Assert.Equal(
            new[]
            {
                NativeTestCapability.DirectorySymbolicLinks,
                NativeTestCapability.FileSymbolicLinks
            },
            capabilities.Order());
    }

    [Theory]
    [InlineData(",")]
    [InlineData("DirectorySymbolicLinks,")]
    [InlineData("UnknownCapability")]
    [InlineData("FileSymbolicLinks,filesymboliclinks")]
    public void ParseRequiredCapabilities_InvalidValue_FailsClosed(string value)
    {
        Assert.Throws<FormatException>(() =>
            NativeTestCapabilityPolicy.ParseRequiredCapabilities(value));
    }

    [Fact]
    public void GetExecutionDecision_RequiredUnavailableCapability_StillRuns()
    {
        var required = new HashSet<NativeTestCapability>
        {
            NativeTestCapability.DirectorySymbolicLinks
        };

        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks,
            required,
            capability => NativeTestCapabilityProbeResult.Unavailable(
                capability,
                "not available"));

        Assert.True(decision.ShouldRun);
        Assert.Null(decision.SkipReason);
    }

    [Fact]
    public void GetExecutionDecision_OptionalUnavailableCapability_SkipsPrecisely()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.FileSymbolicLinks,
            new HashSet<NativeTestCapability>(),
            capability => NativeTestCapabilityProbeResult.Unavailable(
                capability,
                "permission denied"));

        Assert.False(decision.ShouldRun);
        Assert.Contains("File symbolic links", decision.SkipReason);
        Assert.Contains("permission denied", decision.SkipReason);
    }

    [Fact]
    public void GetExecutionDecision_DirectoryAvailability_DoesNotImplyFileAvailability()
    {
        var probes = new List<NativeTestCapability>();
        NativeTestCapabilityProbeResult Probe(NativeTestCapability capability)
        {
            probes.Add(capability);
            return capability == NativeTestCapability.DirectorySymbolicLinks
                ? NativeTestCapabilityProbeResult.Available(capability)
                : NativeTestCapabilityProbeResult.Unavailable(
                    capability,
                    "file links unavailable");
        }

        var directoryDecision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks,
            new HashSet<NativeTestCapability>(),
            Probe);
        var fileDecision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.FileSymbolicLinks,
            new HashSet<NativeTestCapability>(),
            Probe);

        Assert.True(directoryDecision.ShouldRun);
        Assert.False(fileDecision.ShouldRun);
        Assert.Equal(
            new[]
            {
                NativeTestCapability.DirectorySymbolicLinks,
                NativeTestCapability.FileSymbolicLinks
            },
            probes);
    }

    [Fact]
    public void GetExecutionDecision_CombinedOptionalCapabilities_ReportsMissingFileLinks()
    {
        var probes = new List<NativeTestCapability>();
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            new[]
            {
                NativeTestCapability.DirectorySymbolicLinks,
                NativeTestCapability.FileSymbolicLinks
            },
            new HashSet<NativeTestCapability>(),
            capability =>
            {
                probes.Add(capability);
                return capability == NativeTestCapability.DirectorySymbolicLinks
                    ? NativeTestCapabilityProbeResult.Available(capability)
                    : NativeTestCapabilityProbeResult.Unavailable(
                        capability,
                        "file links unavailable");
            });

        Assert.False(decision.ShouldRun);
        Assert.Contains("File symbolic links", decision.SkipReason);
        Assert.Equal(
            new[]
            {
                NativeTestCapability.DirectorySymbolicLinks,
                NativeTestCapability.FileSymbolicLinks
            },
            probes);
    }

    [Fact]
    public void GetExecutionDecision_CombinedRequiredCapabilities_RunWithoutDiscoveryProbe()
    {
        var required = new HashSet<NativeTestCapability>
        {
            NativeTestCapability.DirectorySymbolicLinks,
            NativeTestCapability.FileSymbolicLinks
        };

        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            required.ToArray(),
            required,
            _ => throw new InvalidOperationException(
                "Required capabilities must be proved by the suite preflight, not discovery."));

        Assert.True(decision.ShouldRun);
        Assert.Null(decision.SkipReason);
    }

    [Fact]
    public void GetExecutionDecision_EmptyCapabilitySet_FailsClosed()
    {
        Assert.Throws<ArgumentException>(() =>
            NativeTestCapabilityPolicy.GetExecutionDecision(
                Array.Empty<NativeTestCapability>(),
                new HashSet<NativeTestCapability>(),
                NativeTestCapabilityPolicy.Probe));
    }
}

[Trait("Name", "NativeTestWorkflowContractTests")]
[Trait("Category", "Architecture")]
public sealed class NativeTestWorkflowContractTests : BaseTests
{
    private const string RequiredCapabilities =
        "DirectorySymbolicLinks,FileSymbolicLinks";

    [Fact]
    public void NativeBackendJobs_UseSharedFailClosedFullSuiteContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = NormalizeLineEndings(File.ReadAllText(Path.Join(
            repositoryRoot,
            ".github",
            "workflows",
            "run-tests.yml")));
        var linuxJob = ExtractJob(workflow, "unit-tests", "backend-tests-windows");
        var windowsJob = ExtractJob(workflow, "backend-tests-windows", null);

        AssertNativeJobContract(linuxJob, "ubuntu-24.04");
        AssertNativeJobContract(windowsJob, "windows-2025");
    }

    [Fact]
    public void NativeBackendRunner_PerformsPreflightThenUnfilteredFullSuite()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = NormalizeLineEndings(File.ReadAllText(Path.Join(
            repositoryRoot,
            "scripts",
            "run-native-backend-tests.ps1")));

        Assert.Contains(
            "$env:LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeTestCapabilityContractTests.RequiredNativeTestCapabilities_AreAvailable",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test tests/Listenarr.Tests.csproj",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test listenarr.slnx",
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(script, @"(?m)^\s*& dotnet test ").Count);

        var fullSuiteIndex = script.IndexOf(
            "& dotnet test listenarr.slnx",
            StringComparison.Ordinal);
        Assert.True(fullSuiteIndex >= 0);
        var fullSuiteCommand = script[fullSuiteIndex..];
        Assert.DoesNotContain("--filter", fullSuiteCommand, StringComparison.Ordinal);
        Assert.Contains(
            "$exitCode = $LASTEXITCODE",
            fullSuiteCommand,
            StringComparison.Ordinal);
        Assert.Contains("exit $exitCode", fullSuiteCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityEnvironmentVariable_IsOwnedBySharedPolicyAndNativeWorkflow()
    {
        var repositoryRoot = FindRepositoryRoot();
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Join(
                repositoryRoot,
                "tests",
                "Common",
                "NativeTestCapabilities.cs")),
            Path.GetFullPath(Path.Join(
                repositoryRoot,
                "scripts",
                "run-native-backend-tests.ps1")),
            Path.GetFullPath(Path.Join(
                repositoryRoot,
                ".github",
                "workflows",
                "run-tests.yml")),
            Path.GetFullPath(Path.Join(
                repositoryRoot,
                "tests",
                "Features",
                "Architecture",
                "NativeTestCapabilityContractTests.cs"))
        };
        var violations = EnumerateContractSourceFiles(repositoryRoot)
            .Where(path => !allowedFiles.Contains(Path.GetFullPath(path)))
            .Where(path => File.ReadAllText(path).Contains(
                NativeTestCapabilityPolicy.RequiredCapabilitiesEnvironmentVariable,
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "The native capability environment contract may only be read by the shared policy and declared by the shared CI path:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DeprecatedDirectoryOnlyCapabilitySwitch_IsAbsent()
    {
        var repositoryRoot = FindRepositoryRoot();
        var deprecatedVariable = string.Concat(
            "LISTENARR_REQUIRE_",
            "DIRECTORY_LINK_TESTS");
        var violations = EnumerateContractSourceFiles(repositoryRoot)
            .Where(path => File.ReadAllText(path).Contains(
                deprecatedVariable,
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "The deprecated directory-only capability switch must not return:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LinkCapabilityAttributes_AreDefinedOnlyBySharedPlatformAttributes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var platformAttributesPath = Path.GetFullPath(Path.Join(
            repositoryRoot,
            "tests",
            "Common",
            "PlatformFactAttributes.cs"));
        var declarationPattern = new Regex(
            @"\bclass\s+\w*(?:Directory|File)\w*Link\w*(?:Fact|Theory)Attribute\b",
            RegexOptions.CultureInvariant);
        var violations = EnumerateContractSourceFiles(repositoryRoot)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                platformAttributesPath,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => declarationPattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Link capability attributes must delegate through tests/Common/PlatformFactAttributes.cs:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void SymbolicLinkTests_DeclareExactNativeCapabilities()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testsRoot = Path.Join(repositoryRoot, "tests");
        var sources = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(path => new NativeCapabilityTestSourceAnalyzer.NativeCapabilitySource(
                Path.GetRelativePath(repositoryRoot, path),
                File.ReadAllText(path)))
            .ToArray();
        var violations = NativeCapabilityTestSourceAnalyzer
            .AnalyzeSources(sources)
            .Select(violation =>
                $"{violation.SourcePath}:{violation.Line} "
                + $"{violation.MethodName}: missing {violation.MissingCapability} capability attribute")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Tests that create symbolic links must declare the exact native capability, "
            + "including link creation reached through local helper methods:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> EnumerateContractSourceFiles(
        string repositoryRoot)
    {
        return Directory
            .EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNativeJobContract(
        string job,
        string expectedRunner)
    {
        Assert.Contains($"runs-on: {expectedRunner}", job, StringComparison.Ordinal);
        Assert.Contains(
            $"LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES: '{RequiredCapabilities}'",
            job,
            StringComparison.Ordinal);
        Assert.Contains(
            "pwsh -NoProfile -File scripts/run-native-backend-tests.ps1",
            job,
            StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", job, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", job, StringComparison.Ordinal);
    }

    private static string ExtractJob(
        string workflow,
        string jobName,
        string? nextJobName)
    {
        var startMarker = $"  {jobName}:\n";
        var start = workflow.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow job '{jobName}' was not found.");
        if (nextJobName == null)
        {
            return workflow[start..];
        }

        var endMarker = $"  {nextJobName}:\n";
        var end = workflow.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Workflow job '{nextJobName}' was not found after '{jobName}'.");
        return workflow[start..end];
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

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
