namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static Task ReportProgressAsync(
        AudiobookContentMoveRequest request,
        double progress,
        string phase,
        CancellationToken cancellationToken) =>
        request.ProgressReporter?.Invoke(
            Math.Clamp(progress, 0, 100),
            phase,
            cancellationToken) ?? Task.CompletedTask;

    private static double CalculateWeightedProgress(
        double start,
        double span,
        long completedUnits,
        long totalUnits)
    {
        if (totalUnits <= 0)
        {
            return start + span;
        }

        return start + (span * Math.Clamp(
            (double)completedUnits / totalUnits,
            0,
            1));
    }

    private static long GetProgressUnits(MoveJobEntry entry) =>
        Math.Max(entry.Length, 1);
}
