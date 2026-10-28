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

using Listenarr.Api.Startup;
using Listenarr.Tests.Common;
using Serilog.Events;

namespace Listenarr.Tests.Features.Api.Startup;

public sealed class ListenarrBuilderFactoryTests
{
    [Fact]
    public void EnsureExternalConfiguration_CreatesDefaultConfigurationInsideContentRoot()
    {
        var contentRoot = CreateTemporaryDirectory();
        var expectedPath = Path.Join(contentRoot, "config", "appsettings", "appsettings.json");

        try
        {
            ListenarrBuilderFactory.EnsureExternalConfiguration(contentRoot, new LocalFileSystem());

            Assert.True(File.Exists(expectedPath));
            Assert.Contains("\"Serilog\"", File.ReadAllText(expectedPath), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(contentRoot);
        }
    }

    [Fact]
    public void EnsureExternalConfiguration_ValidatesAgainstContentRoot()
    {
        var contentRoot = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"listenarr-startup-{Guid.NewGuid():N}"));
        var configDirectory = Path.Join(contentRoot, "config", "appsettings");
        var expectedPath = Path.Join(configDirectory, "appsettings.json");
        var safePath = Path.GetFullPath(expectedPath);
        var reason = string.Empty;
        var fileSystem = new Mock<IFileSystem>();

        fileSystem.Setup(fs => fs.DirectoryExists(configDirectory)).Returns(true);
        fileSystem.Setup(fs => fs.FileExists(expectedPath)).Returns(false);
        fileSystem
            .Setup(fs => fs.TryValidateMutationTarget(
                expectedPath,
                It.Is<IEnumerable<string?>>(roots =>
                    roots.SequenceEqual(new string?[] { contentRoot }, StringComparer.OrdinalIgnoreCase)),
                out safePath,
                out reason))
            .Returns(true);

        ListenarrBuilderFactory.EnsureExternalConfiguration(contentRoot, fileSystem.Object);

        fileSystem.Verify(fs => fs.WriteAllText(safePath, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void EnsureExternalConfiguration_DoesNotWriteRejectedTarget()
    {
        var contentRoot = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"listenarr-startup-{Guid.NewGuid():N}"));
        var configDirectory = Path.Join(contentRoot, "config", "appsettings");
        var expectedPath = Path.Join(configDirectory, "appsettings.json");
        var safePath = string.Empty;
        var reason = "Target resolves outside all allowed mutation roots.";
        var fileSystem = new Mock<IFileSystem>();

        fileSystem.Setup(fs => fs.DirectoryExists(configDirectory)).Returns(true);
        fileSystem.Setup(fs => fs.FileExists(expectedPath)).Returns(false);
        fileSystem
            .Setup(fs => fs.TryValidateMutationTarget(
                expectedPath,
                It.IsAny<IEnumerable<string?>>(),
                out safePath,
                out reason))
            .Returns(false);

        ListenarrBuilderFactory.EnsureExternalConfiguration(contentRoot, fileSystem.Object);

        fileSystem.Verify(
            fs => fs.WriteAllText(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [DirectoryLinkFact]
    public void EnsureExternalConfiguration_BlocksSymlinkedConfigDirectoryEscape()
    {
        var contentRoot = CreateTemporaryDirectory();
        var outsideRoot = CreateTemporaryDirectory();
        var configRoot = Path.Join(contentRoot, "config");
        var linkedAppSettings = Path.Join(configRoot, "appsettings");
        var outsideConfigPath = Path.Join(outsideRoot, "appsettings.json");

        try
        {
            Directory.CreateDirectory(configRoot);

            try
            {
                Directory.CreateSymbolicLink(linkedAppSettings, outsideRoot);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"This native filesystem regression requires symbolic-link support: {exception.Message}");
            }

            Assert.True(
                Directory.Exists(linkedAppSettings),
                "The symbolic-link directory must be visible before the escape check runs.");

            ListenarrBuilderFactory.EnsureExternalConfiguration(contentRoot, new LocalFileSystem());

            Assert.False(File.Exists(outsideConfigPath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(linkedAppSettings))
                {
                    Directory.Delete(linkedAppSettings);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            DeleteDirectory(contentRoot);
            DeleteDirectory(outsideRoot);
        }
    }

    [Theory]
    [InlineData("Trace", LogEventLevel.Verbose)]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("TRACE", LogEventLevel.Verbose)]
    [InlineData("  Trace  ", LogEventLevel.Verbose)]
    [InlineData("Critical", LogEventLevel.Fatal)]
    [InlineData("critical", LogEventLevel.Fatal)]
    [InlineData("CRITICAL", LogEventLevel.Fatal)]
    [InlineData("  Critical  ", LogEventLevel.Fatal)]
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    public void TryParseLogLevel_ValidInputsAndAliases_ReturnsTrueAndExpectedLevel(string input, LogEventLevel expected)
    {
        var success = ListenarrBuilderFactory.TryParseLogLevel(input, out var level);

        Assert.True(success);
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("Warn")]
    [InlineData("Info")]
    [InlineData("None")]
    public void TryParseLogLevel_InvalidOrEmptyInputs_ReturnsFalse(string? input)
    {
        var success = ListenarrBuilderFactory.TryParseLogLevel(input, out var level);

        Assert.False(success);
    }

    [Fact]
    public void ResolveMinimumLevel_ValidEnvVar_UsesEnvVarWithoutWarnings()
    {
        var warnings = new List<string>();

        var level = ListenarrBuilderFactory.ResolveMinimumLevel("Trace", "Information", warnings.Add);

        Assert.Equal(LogEventLevel.Verbose, level);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(null, "Debug", LogEventLevel.Debug)]
    [InlineData("", "Trace", LogEventLevel.Verbose)]
    [InlineData("   ", "Warning", LogEventLevel.Warning)]
    public void ResolveMinimumLevel_EnvUnset_FallsBackToConfigWithoutWarnings(string? env, string? config, LogEventLevel expected)
    {
        var warnings = new List<string>();

        var level = ListenarrBuilderFactory.ResolveMinimumLevel(env, config, warnings.Add);

        Assert.Equal(expected, level);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    [InlineData("   ", "   ")]
    public void ResolveMinimumLevel_EnvAndConfigUnset_DefaultsToInformationWithoutWarnings(string? env, string? config)
    {
        var warnings = new List<string>();

        var level = ListenarrBuilderFactory.ResolveMinimumLevel(env, config, warnings.Add);

        Assert.Equal(LogEventLevel.Information, level);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ResolveMinimumLevel_InvalidEnvAndValidConfig_WarnsAndFallsBackToConfig()
    {
        var warnings = new List<string>();

        var level = ListenarrBuilderFactory.ResolveMinimumLevel("bogus", "Debug", warnings.Add);

        Assert.Equal(LogEventLevel.Debug, level);
        var warning = Assert.Single(warnings);
        Assert.Contains("bogus", warning);
        Assert.Contains("LISTENARR_LOG_LEVEL", warning);
        Assert.Contains("Verbose (or Trace)", warning);
    }

    [Fact]
    public void ResolveMinimumLevel_InvalidEnvAndInvalidConfig_WarnsAndDefaultsToInformation()
    {
        var warnings = new List<string>();

        var level = ListenarrBuilderFactory.ResolveMinimumLevel("invalid-env", "invalid-cfg", warnings.Add);

        Assert.Equal(LogEventLevel.Information, level);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("invalid-env", warnings[0]);
        Assert.Contains("LISTENARR_LOG_LEVEL", warnings[0]);
        Assert.Contains("Verbose (or Trace)", warnings[0]);
        Assert.Contains("invalid-cfg", warnings[1]);
        Assert.Contains("Verbose (or Trace)", warnings[1]);
    }

    [Fact]
    public void ResolveMinimumLevel_DefaultWarningLogger_SucceedsWithoutExplicitLogger()
    {
        var level = ListenarrBuilderFactory.ResolveMinimumLevel("Trace", "Information");

        Assert.Equal(LogEventLevel.Verbose, level);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"listenarr-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
