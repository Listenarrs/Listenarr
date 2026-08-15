using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Common;

[Trait("Name", "WindowsPathTestFixtureTests")]
[Trait("Category", "TestInfrastructure")]
public sealed class WindowsPathTestFixtureTests : BaseTests
{
    [WindowsFact]
    public void CreateRootRelativeAliasCompatibleDirectory_UsesRootRelativeResolutionDrive()
    {
        var directory = WindowsPathTestFixture
            .CreateRootRelativeAliasCompatibleDirectory("drive-contract");
        try
        {
            var rootRelativeDrive = Path.GetPathRoot(Path.GetFullPath(
                Path.DirectorySeparatorChar.ToString()));

            Assert.Equal(
                rootRelativeDrive,
                Path.GetPathRoot(directory),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void GetRootRelativeForeignAlias_ProvesSameNativeEndpoint()
    {
        var directory = WindowsPathTestFixture
            .CreateRootRelativeAliasCompatibleDirectory("alias-contract");
        try
        {
            var nativePath = Path.Join(directory, "book.m4b");
            File.WriteAllText(nativePath, "audio");

            var foreignPath = WindowsPathTestFixture
                .GetRootRelativeForeignAlias(nativePath);

            Assert.Equal(
                Path.GetFullPath(nativePath),
                Path.GetFullPath(foreignPath),
                StringComparer.OrdinalIgnoreCase);
            Assert.True(File.Exists(foreignPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
