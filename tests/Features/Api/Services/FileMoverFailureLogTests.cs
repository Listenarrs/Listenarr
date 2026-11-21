using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverFailureLogTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverFailureLogTests : BaseTests
{
    [Fact]
    public async Task PerformActionOn_FailureWithLineBreaksInCauseChain_RendersNoForgedLogLine()
    {
        var root = FileService.GetTempDirectory("mutation-failure-log");
        var source = await FileService.GetFileAsync(root, "source.mp3", "content");
        var destination = Path.Join(root, "destination.mp3");
        var logger = new RenderingLogger();
        var longDirectory = string.Join('/', Enumerable.Repeat("a-long-directory-name", 14));
        var inner = new InvalidDataException("inner cause\nFORGED-INNER entry for /srv/media/secret/inner.mp3");
        var mover = CreateMover(
            logger,
            () => Task.FromException(
                new IOException($"outer cause\r\nFORGED-OUTER entry for /srv/media/secret/outer.mp3 at /{longDirectory}/tail-file-name.mp3", inner)));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mover.PerformActionOn(FileAction.HardlinkCopy, source, destination, Guid.NewGuid()));

        var rendered = Assert.Single(logger.Errors);
        var lines = rendered.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        Assert.DoesNotContain(lines, line => line.StartsWith("FORGED", StringComparison.Ordinal));
        Assert.Contains("FORGED-OUTER entry", rendered);
        Assert.Contains("FORGED-INNER entry", rendered);
        Assert.Contains(typeof(IOException).FullName!, rendered);
        Assert.Contains(typeof(InvalidDataException).FullName!, rendered);
        Assert.Contains("tail-file-name.mp3", rendered);
        Assert.Contains("   at ", rendered);
        Assert.Contains($"{Environment.NewLine} ---> {typeof(InvalidDataException).FullName}", rendered);
        Assert.Contains($"{Environment.NewLine}   --- End of inner exception stack trace ---", rendered);
        Assert.Contains("outer cause\r\nFORGED-OUTER", thrown.InnerException!.Message);
        Assert.Same(inner, thrown.InnerException.InnerException);
    }

    private FileMover CreateMover(ILogger<FileMover> logger, Func<Task> afterTargetCreatedBeforeState)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            logger,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory("mutation-failure-log-locks"),
            AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync = afterTargetCreatedBeforeState
        };
    }

    /// <summary>
    /// Records each Error entry as a sink would write it: the formatted message, then the
    /// exception's ToString().
    /// </summary>
    private sealed class RenderingLogger : ILogger<FileMover>
    {
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors => _errors;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                _errors.Add(formatter(state, exception) + Environment.NewLine + exception);
            }
        }
    }
}
