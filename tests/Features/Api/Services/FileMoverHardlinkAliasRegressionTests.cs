using System.Runtime.InteropServices;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverHardlinkAliasRegressionTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverHardlinkAliasRegressionTests : BaseTests
{
    [Theory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationHardlinkAlias_IsBlocked(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-hardlink-alias");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");
        Assert.True(
            TryCreateHardLink(destination, source),
            "The required hard link could not be created.");

        Assert.False(await CreateMover().PerformActionOn(
            action,
            source,
            destination,
            Guid.NewGuid()));

        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
    }

    private FileMover CreateMover()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            new NullLogger<FileMover>(),
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "file-hardlink-alias-locks")
        };
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateHardLinkWindows(linkPath, existingPath, IntPtr.Zero)
                : LinkUnix(existingPath, linkPath) == 0;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
