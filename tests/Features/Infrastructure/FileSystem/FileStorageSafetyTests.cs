using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem
{
    public class FileStorageSafetyTests : IDisposable
    {
        private readonly string _root;
        private readonly string _outside;
        private readonly FileStorage _storage;

        public FileStorageSafetyTests()
        {
            _root = Path.Join(Path.GetTempPath(), "listenarr-storage-root-" + Guid.NewGuid().ToString("N"));
            _outside = Path.Join(Path.GetTempPath(), "listenarr-storage-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_outside);

            var appPaths = new Mock<IApplicationPathService>();
            appPaths.SetupGet(paths => paths.ContentRootPath).Returns(_root);
            appPaths.SetupGet(paths => paths.ConfigRootPath).Returns(Path.Join(_root, "config"));
            _storage = new FileStorage(appPaths.Object, new NullLogger<FileStorage>());
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            try { Directory.Delete(_outside, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        [Fact]
        public async Task WriteAllTextAsync_AllowsConfiguredRoot()
        {
            var path = Path.Join(_root, "config", "settings.json");

            await _storage.WriteAllTextAsync(path, "{}");

            Assert.True(File.Exists(path));
        }

        [Fact]
        public async Task WriteAllTextAsync_BlocksOutsideConfiguredRoots()
        {
            var path = Path.Join(_outside, "settings.json");

            await Assert.ThrowsAsync<IOException>(() => _storage.WriteAllTextAsync(path, "{}"));

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void DeleteDirectory_BlocksSiblingPrefixPath()
        {
            var sibling = _root + "-sibling";
            Directory.CreateDirectory(sibling);

            try
            {
                Assert.Throws<IOException>(() => _storage.DeleteDirectory(sibling, recursive: true));
                Assert.True(Directory.Exists(sibling));
            }
            finally
            {
                try { Directory.Delete(sibling, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }
    }
}
