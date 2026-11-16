using System.Diagnostics;

namespace Listenarr.Tests.Common;

/// <summary>
/// Answers the <c>ffprobe -version</c> check FfmpegService runs before it uses a binary, so tests
/// that place a fake binary can still reach the code they are about.
/// </summary>
internal static class FfprobeProcessRunnerMockExtensions
{
    public const string VersionOutput = "ffprobe version 9.0.2 Copyright (c) 2007-2025 the FFmpeg developers\nbuilt with gcc\n";

    public static Mock<IProcessRunner> AnswerFfprobeVersionCheck(
        this Mock<IProcessRunner> processRunner,
        ProcessResult? result = null)
    {
        processRunner.Setup(runner => runner.RunAsync(
                It.Is<ProcessStartInfo>(startInfo => IsVersionCheck(startInfo)),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? new ProcessResult(0, VersionOutput, string.Empty, false));
        return processRunner;
    }

    public static bool IsVersionCheck(ProcessStartInfo startInfo)
    {
        return startInfo.ArgumentList.Count == 1 && startInfo.ArgumentList[0] == "-version";
    }
}
