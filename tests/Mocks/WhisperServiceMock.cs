using Listenarr.Application.Audiobooks.Verification.Contracts;

namespace Listenarr.Tests.Mocks
{
    /// <summary>
    /// Whisper stub for tests: reports available and returns a fixed transcript
    /// (settable per test). Avoids any dependency on a real whisper.cpp binary.
    /// </summary>
    public class WhisperServiceMock : IWhisperService
    {
        public bool Available { get; set; } = true;
        public string? Transcript { get; set; } = null;

        public string ModelName => "mock";

        public Task<bool> IsAvailableAsync() => Task.FromResult(Available);

        public Task<string?> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
            => Task.FromResult(Transcript);
    }
}
