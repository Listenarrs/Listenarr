namespace Listenarr.Application.Audiobooks.Jobs;

public static class MoveTimingPolicy
{
    public const int MaxTransientAttempts = 5;
    public static readonly TimeSpan OwnershipDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(2);

    public static TimeSpan GetRetryDelay(Guid jobId, int attemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptCount, 1);
        var exponentialSeconds = BaseRetryDelay.TotalSeconds
            * Math.Pow(2, Math.Min(attemptCount - 1, 10));
        var bytes = jobId.ToByteArray();
        var seed = BitConverter.ToUInt32(bytes, 0) ^ (uint)attemptCount;
        var jitterFactor = 0.8 + ((seed % 4001) / 10000d);
        var delayedSeconds = exponentialSeconds * jitterFactor;
        return TimeSpan.FromSeconds(
            Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }
}
