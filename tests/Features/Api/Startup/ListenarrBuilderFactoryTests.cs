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
