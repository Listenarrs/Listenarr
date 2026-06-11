using System.Runtime.InteropServices;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Services
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
    }
}
