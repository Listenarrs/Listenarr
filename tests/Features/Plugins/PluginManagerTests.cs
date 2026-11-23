using System.IO;
using System.IO.Compression;
using Listenarr.Api.Plugins;

namespace Listenarr.Tests.Features.Plugins;

public class PluginManagerTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "listenarr-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SafeExtract_ExtractsNormalEntries()
    {
        var work = NewTempDir();
        try
        {
            var zip = Path.Combine(work, "ok.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                // Create mode allows only one open entry stream at a time — close each before the next.
                using (var w = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
                {
                    w.Write("{\"id\":\"demo\"}");
                }

                using (var w2 = new StreamWriter(archive.CreateEntry("ui/app.js").Open()))
                {
                    w2.Write("console.log(1)");
                }
            }

            var dest = Path.Combine(work, "out");
            PluginManager.SafeExtract(zip, dest);

            Assert.True(File.Exists(Path.Combine(dest, "plugin.json")));
            Assert.True(File.Exists(Path.Combine(dest, "ui", "app.js")));
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    [Fact]
    public void SafeExtract_RejectsZipSlip()
    {
        var work = NewTempDir();
        try
        {
            var zip = Path.Combine(work, "evil.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                // Entry name escapes the destination directory.
                using var w = new StreamWriter(archive.CreateEntry("../escape.txt").Open());
                w.Write("pwned");
            }

            var dest = Path.Combine(work, "out");
            Assert.Throws<InvalidOperationException>(() => PluginManager.SafeExtract(zip, dest));
            // Nothing should have been written outside the destination.
            Assert.False(File.Exists(Path.Combine(work, "escape.txt")));
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }
}
