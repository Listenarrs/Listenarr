using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem
{
    public class ArchiveExtractorSafetyTests : IDisposable
    {
        private readonly string _root;

        public ArchiveExtractorSafetyTests()
        {
            _root = Path.Join(Path.GetTempPath(), "listenarr-archive-safety-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        [Fact]
        public async Task ExtractArchiveToTempDirAsync_SkipsTraversalEntries()
        {
            var archivePath = Path.Join(_root, "payload.zip");
            var escapedPath = Path.Join(_root, "escaped.mp3");

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var safeEntry = archive.CreateEntry("album/track.mp3");
                await using (var stream = safeEntry.Open())
                await using (var writer = new StreamWriter(stream))
                {
                    await writer.WriteAsync("safe");
                }

                var traversalEntry = archive.CreateEntry("../escaped.mp3");
                await using (var stream = traversalEntry.Open())
                await using (var writer = new StreamWriter(stream))
                {
                    await writer.WriteAsync("escape");
                }

                var rootedEntry = archive.CreateEntry("/rooted.mp3");
                await using (var stream = rootedEntry.Open())
                await using (var writer = new StreamWriter(stream))
                {
                    await writer.WriteAsync("rooted");
                }
            }

            var extractor = new ArchiveExtractor(new NullLogger<ArchiveExtractor>());
            using var extracted = await extractor.ExtractArchiveToTempDirAsync(archivePath);

            Assert.NotNull(extracted);
            Assert.True(File.Exists(Path.Join(extracted!.Path, "album", "track.mp3")));
            Assert.False(File.Exists(Path.Join(extracted.Path, "escaped.mp3")));
            Assert.False(File.Exists(Path.Join(extracted.Path, "rooted.mp3")));
            Assert.False(File.Exists(escapedPath));
        }
    }
}
