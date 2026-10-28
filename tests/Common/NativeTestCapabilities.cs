using System.Runtime.InteropServices;
using System.Security;

namespace Listenarr.Tests.Common;

internal enum NativeTestCapability
{
    DirectorySymbolicLinks,
    FileSymbolicLinks
}

internal readonly record struct NativeTestCapabilityProbeResult(
    NativeTestCapability Capability,
    bool IsAvailable,
    string? FailureReason)
{
    public static NativeTestCapabilityProbeResult Available(
        NativeTestCapability capability) =>
        new(capability, true, null);

    public static NativeTestCapabilityProbeResult Unavailable(
        NativeTestCapability capability,
        string failureReason) =>
        new(capability, false, failureReason);
}

internal readonly record struct NativeTestExecutionDecision(
    bool ShouldRun,
    string? SkipReason);

internal static class NativeTestCapabilityPolicy
{
    internal const string RequiredCapabilitiesEnvironmentVariable =
        "LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES";

    private static readonly IReadOnlyDictionary<string, NativeTestCapability>
        CapabilityNames = new Dictionary<string, NativeTestCapability>(
            StringComparer.OrdinalIgnoreCase)
        {
            [nameof(NativeTestCapability.DirectorySymbolicLinks)] =
                NativeTestCapability.DirectorySymbolicLinks,
            [nameof(NativeTestCapability.FileSymbolicLinks)] =
                NativeTestCapability.FileSymbolicLinks
        };

    internal static IReadOnlySet<NativeTestCapability> GetRequiredCapabilities() =>
        ParseRequiredCapabilities(
            Environment.GetEnvironmentVariable(
                RequiredCapabilitiesEnvironmentVariable));

    internal static IReadOnlySet<NativeTestCapability> ParseRequiredCapabilities(
        string? value)
    {
        var capabilities = new HashSet<NativeTestCapability>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return capabilities;
        }

        foreach (var segment in value.Split(',', StringSplitOptions.None))
        {
            var name = segment.Trim();
            if (name.Length == 0)
            {
                throw new FormatException(
                    $"{RequiredCapabilitiesEnvironmentVariable} contains an empty capability name.");
            }

            if (!CapabilityNames.TryGetValue(name, out var capability))
            {
                throw new FormatException(
                    $"{RequiredCapabilitiesEnvironmentVariable} contains unsupported capability '{name}'. "
                    + $"Supported values: {string.Join(", ", CapabilityNames.Keys.Order(StringComparer.Ordinal))}.");
            }

            if (!capabilities.Add(capability))
            {
                throw new FormatException(
                    $"{RequiredCapabilitiesEnvironmentVariable} contains duplicate capability '{name}'.");
            }
        }

        return capabilities;
    }

    internal static NativeTestExecutionDecision GetExecutionDecision(
        NativeTestCapability capability) =>
        GetExecutionDecision(
            new[] { capability },
            GetRequiredCapabilities(),
            Probe);

    internal static NativeTestExecutionDecision GetExecutionDecision(
        params NativeTestCapability[] capabilities) =>
        GetExecutionDecision(
            capabilities,
            GetRequiredCapabilities(),
            Probe);

    internal static NativeTestExecutionDecision GetExecutionDecision(
        NativeTestCapability capability,
        IReadOnlySet<NativeTestCapability> requiredCapabilities,
        Func<NativeTestCapability, NativeTestCapabilityProbeResult> probe) =>
        GetExecutionDecision(
            new[] { capability },
            requiredCapabilities,
            probe);

    internal static NativeTestExecutionDecision GetExecutionDecision(
        IReadOnlyCollection<NativeTestCapability> capabilities,
        IReadOnlySet<NativeTestCapability> requiredCapabilities,
        Func<NativeTestCapability, NativeTestCapabilityProbeResult> probe)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count == 0)
        {
            throw new ArgumentException(
                "At least one native test capability is required.",
                nameof(capabilities));
        }

        foreach (var capability in capabilities.Distinct().Order())
        {
            if (requiredCapabilities.Contains(capability))
            {
                continue;
            }

            var result = probe(capability);
            if (!result.IsAvailable)
            {
                return new NativeTestExecutionDecision(
                    false,
                    $"{GetDisplayName(capability)} are unavailable on this optional test runner: "
                    + result.FailureReason);
            }
        }

        return new NativeTestExecutionDecision(true, null);
    }

    internal static IReadOnlyList<NativeTestCapabilityProbeResult>
        ProbeRequiredCapabilities()
    {
        return GetRequiredCapabilities()
            .Order()
            .Select(Probe)
            .Where(result => !result.IsAvailable)
            .ToArray();
    }

    internal static void RequireAvailable(NativeTestCapability capability)
    {
        var result = Probe(capability);
        if (!result.IsAvailable)
        {
            throw new Xunit.Sdk.XunitException(
                $"Required native capability '{capability}' became unavailable during test execution: "
                + result.FailureReason);
        }
    }

    internal static NativeTestCapabilityProbeResult Probe(
        NativeTestCapability capability) =>
        capability switch
        {
            NativeTestCapability.DirectorySymbolicLinks =>
                ProbeDirectorySymbolicLinks(),
            NativeTestCapability.FileSymbolicLinks =>
                ProbeFileSymbolicLinks(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "Unknown native test capability.")
        };

    internal static string DescribeHost()
    {
        var runnerImage = Environment.GetEnvironmentVariable("ImageOS");
        var runnerImageVersion = Environment.GetEnvironmentVariable("ImageVersion");
        var runnerOs = Environment.GetEnvironmentVariable("RUNNER_OS");
        var runnerArchitecture = Environment.GetEnvironmentVariable("RUNNER_ARCH");
        return string.Join(
            ", ",
            new[]
            {
                $"OS={RuntimeInformation.OSDescription}",
                $"Architecture={RuntimeInformation.OSArchitecture}",
                string.IsNullOrWhiteSpace(runnerOs) ? null : $"RunnerOS={runnerOs}",
                string.IsNullOrWhiteSpace(runnerArchitecture)
                    ? null
                    : $"RunnerArchitecture={runnerArchitecture}",
                string.IsNullOrWhiteSpace(runnerImage) ? null : $"ImageOS={runnerImage}",
                string.IsNullOrWhiteSpace(runnerImageVersion)
                    ? null
                    : $"ImageVersion={runnerImageVersion}"
            }.Where(value => value != null));
    }

    private static NativeTestCapabilityProbeResult ProbeDirectorySymbolicLinks()
    {
        var capability = NativeTestCapability.DirectorySymbolicLinks;
        var root = Path.Join(
            Path.GetTempPath(),
            $"listenarr-directory-link-capability-{Guid.NewGuid():N}");
        var target = Path.Join(root, "target");
        var link = Path.Join(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
            var attributes = File.GetAttributes(link);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return NativeTestCapabilityProbeResult.Unavailable(
                    capability,
                    "Directory.CreateSymbolicLink did not create a reparse point.");
            }

            if (Directory.ResolveLinkTarget(link, returnFinalTarget: true) == null)
            {
                return NativeTestCapabilityProbeResult.Unavailable(
                    capability,
                    "Directory.ResolveLinkTarget could not resolve the created link.");
            }

            return NativeTestCapabilityProbeResult.Available(capability);
        }
        catch (Exception exception) when (IsCapabilityException(exception))
        {
            return NativeTestCapabilityProbeResult.Unavailable(
                capability,
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            TryDeleteDirectoryLink(link);
            TryDeleteDirectory(target);
            TryDeleteDirectory(root);
        }
    }

    private static NativeTestCapabilityProbeResult ProbeFileSymbolicLinks()
    {
        var capability = NativeTestCapability.FileSymbolicLinks;
        var root = Path.Join(
            Path.GetTempPath(),
            $"listenarr-file-link-capability-{Guid.NewGuid():N}");
        var target = Path.Join(root, "target.bin");
        var link = Path.Join(root, "link.bin");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "capability");
            File.CreateSymbolicLink(link, target);
            var attributes = File.GetAttributes(link);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return NativeTestCapabilityProbeResult.Unavailable(
                    capability,
                    "File.CreateSymbolicLink did not create a reparse point.");
            }

            if (File.ResolveLinkTarget(link, returnFinalTarget: true) == null)
            {
                return NativeTestCapabilityProbeResult.Unavailable(
                    capability,
                    "File.ResolveLinkTarget could not resolve the created link.");
            }

            return NativeTestCapabilityProbeResult.Available(capability);
        }
        catch (Exception exception) when (IsCapabilityException(exception))
        {
            return NativeTestCapabilityProbeResult.Unavailable(
                capability,
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            TryDeleteFile(link);
            TryDeleteFile(target);
            TryDeleteDirectory(root);
        }
    }

    private static string GetDisplayName(NativeTestCapability capability) =>
        capability switch
        {
            NativeTestCapability.DirectorySymbolicLinks => "Directory symbolic links",
            NativeTestCapability.FileSymbolicLinks => "File symbolic links",
            _ => capability.ToString()
        };

    private static bool IsCapabilityException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException;

    private static void TryDeleteDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (IsCapabilityException(exception))
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsCapabilityException(exception))
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (IsCapabilityException(exception))
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }
}
