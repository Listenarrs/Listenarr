namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static void TryRetireUncommittedMarkerlessFile(
        PinnedDirectoryCreation.PinnedFileEntry file)
    {
        try
        {
            if (file.VisiblePathMatches())
            {
                file.Delete();
            }
        }
        catch (Exception exception) when (
            WorkerExceptionClassifier.IsNonFatal(exception))
        {
            // If persistence failed after final-name creation, preserve anything that
            // cannot still be proven to be this exact newly-created file.
        }
    }
}
