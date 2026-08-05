using System.Runtime.InteropServices;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Configuration.Paths
{
    [Trait("Name", "RemotePathMappingServiceTests")]
    [Trait("Category", "RemotePathMapping")]
    public class RemotePathMappingServiceTests : BaseTests
    {
        private IRemotePathMappingService remotePathMappingService = null!;
        private DownloadClientConfiguration client = null!;
        private DownloadClientConfiguration randomClient = null!;

        private class PathTestData : TheoryData<string, string, string, string>
        {
            public PathTestData()
            {
                AddRaw("/downloads", "/media/drive1", "/downloads/test", "/media/drive1/test");
                AddRaw("/downloads", "/media/drive1", "/media/drive1/test", "/media/drive1/test");
                AddRaw("/downloads", "/media/drive1", "/downloads/test/drive", "/media/drive1/test/drive");
                AddRaw("/downloads", "/media/drive1", "test/downloads/test/drive", "test/downloads/test/drive");
                AddRaw("/downloads", "/media/drive1", "/media/drive1", "/media/drive1");
                AddRaw("/downloads .  ", "/media/drive  1  ", "/media/drive1", "/media/drive1");

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    AddRaw("/downloads .  ", "/media/drive  1  ", "/downloads .  ", "/media/drive  1  /");
                    AddRaw("/downloads .  ", "/media/drive  1  ", "/downloads .  / test ", "/media/drive  1  / test ");
                }
            }

            private void AddRaw(string source, string dest, string input, string expected)
            {
                Add(Normalize(source), Normalize(dest), Normalize(input), Normalize(expected));
            }

            // Make sure given path are adapted for the current platform running the test
            private static string Normalize(string path)
            {
                path = path.TrimStart('/');
                path = path.Replace('/', Path.DirectorySeparatorChar);
                path = FileUtils.GetAbsolutePath(path);
                return path;
            }
        }

        public override async Task InitializeAsync()
        {
            remotePathMappingService = _provider.GetRequiredService<IRemotePathMappingService>();
            client = await CreateDownloadClientConfiguration();
            randomClient = await CreateDownloadClientConfiguration();
        }

        [Fact]
        public async Task CreateAsync_AmbiguousForwardSlashDoubleRoot_RejectsUnusableMapping()
        {
            var mapping = new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath("//server/share/downloads")
                .WithLocalPath(FileUtils.GetAbsolutePath("remote-ambiguous-create"))
                .Build();

            await Assert.ThrowsAsync<ArgumentException>(() =>
                remotePathMappingService.CreateAsync(mapping));
        }

        [Fact]
        public async Task UpdateAsync_AmbiguousForwardSlashDoubleRoot_RejectsUnusableMapping()
        {
            var existing = await remotePathMappingService.CreateAsync(
                new RemotePathMappingBuilder()
                    .WithDownloadClientConfiguration(client)
                    .WithRemotePath("/downloads")
                    .WithLocalPath(FileUtils.GetAbsolutePath("remote-ambiguous-update"))
                    .Build());
            existing.RemotePath = "//server/share/downloads";

            await Assert.ThrowsAsync<ArgumentException>(() =>
                remotePathMappingService.UpdateAsync(existing));
        }

        [Theory]
        [ClassData(typeof(PathTestData))]
        [Trait("Method", "TranslatePathAsync")]
        public async Task TranslatePathAsync_HappyPath(string remotePath, string localPath, string given, string expected)
        {
            Assert.Equal(given, await remotePathMappingService.TranslatePathAsync(client, given));

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(remotePath)
                .WithLocalPath(localPath)
                .Build());

            Assert.Equal(expected, await remotePathMappingService.TranslatePathAsync(client, given));
            Assert.Equal(given, await remotePathMappingService.TranslatePathAsync(randomClient, given));
        }

        [Fact]
        [Trait("Method", "TranslatePathAsync")]
        public async Task TranslatePathAsync_DoesNotMapSiblingPrefixPath()
        {
            var remotePath = FileUtils.GetAbsolutePath("downloads");
            var localPath = FileUtils.GetAbsolutePath("imports");
            var siblingPath = FileUtils.GetAbsolutePath("downloads2", "book.m4b");

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(remotePath)
                .WithLocalPath(localPath)
                .Build());

            var translated = await remotePathMappingService.TranslatePathAsync(client, siblingPath);

            Assert.Equal(siblingPath, translated);
        }

        [Fact]
        [Trait("Method", "TranslatePathAsync")]
        public async Task TranslatePathAsync_MapsExactRootAndSeparatorBoundChild()
        {
            var remotePath = FileUtils.GetAbsolutePath("downloads");
            var localPath = FileUtils.GetAbsolutePath("imports");
            var childPath = Path.Join(remotePath, "book.m4b");

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(remotePath)
                .WithLocalPath(localPath)
                .Build());

            Assert.Equal(FileUtils.EnsureTrailingSeparator(FileUtils.NormalizeStoredPath(localPath)), await remotePathMappingService.TranslatePathAsync(client, remotePath));
            Assert.Equal(Path.Join(localPath, "book.m4b"), await remotePathMappingService.TranslatePathAsync(client, childPath));
        }

        [Fact]
        public async Task TranslatePathAsync_BackslashUncRoot_PreservesWindowsRemoteSyntax()
        {
            var localPath = FileUtils.GetAbsolutePath("remote-unc-imports");
            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(@"\\server\share\downloads")
                .WithLocalPath(localPath)
                .Build());

            var translated = await remotePathMappingService.TranslatePathAsync(
                client,
                @"\\server\share\downloads\Author\book.m4b");

            Assert.Equal(Path.Join(localPath, "Author", "book.m4b"), translated);
        }

        [Fact]
        public async Task TranslatePathAsync_ForwardSlashDoubleRoot_IsAmbiguousAndNotMapped()
        {
            var localPath = FileUtils.GetAbsolutePath("remote-ambiguous-imports");
            const string ambiguousRoot = "//server/share/downloads";
            const string reportedPath = "//server/share/downloads/Author/book.m4b";
            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(ambiguousRoot)
                .WithLocalPath(localPath)
                .Build());

            var translated = await remotePathMappingService.TranslatePathAsync(
                client,
                reportedPath);

            Assert.Equal(reportedPath, translated);
        }

        [WindowsFact]
        public async Task TranslatePathAsync_ForeignPersistedLocalRoot_DoesNotMapWindowsAlias()
        {
            var nativeLocalRoot = FileUtils.GetAbsolutePath("foreign-local-root");
            var foreignLocalRoot = TempFileService
                .GetWindowsRootRelativeForeignAlias(nativeLocalRoot);

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath("/downloads")
                .WithLocalPath(foreignLocalRoot)
                .Build());

            var reportedPath = "/downloads/Author/book.m4b";
            var translated = await remotePathMappingService.TranslatePathAsync(
                client,
                reportedPath);

            Assert.Equal(reportedPath, translated);
        }

        [Theory]
        [InlineData("C:/downloads", "C:\\downloads\\Author\\book.m4b")]
        [InlineData("/downloads", "/downloads/Author/book.m4b")]
        public async Task TranslatePathAsync_UsesRemoteSyntaxIndependentOfHost(
            string remoteRoot,
            string reportedPath)
        {
            var localPath = FileUtils.GetAbsolutePath("remote-syntax-imports");
            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(remoteRoot)
                .WithLocalPath(localPath)
                .Build());

            var translated = await remotePathMappingService.TranslatePathAsync(client, reportedPath);

            Assert.Equal(Path.Join(localPath, "Author", "book.m4b"), translated);
        }
    }
}
