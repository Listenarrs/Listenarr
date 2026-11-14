namespace Listenarr.Application.Audiobooks.Files;

// Why a guarded metadata refresh did or did not write the file row.
public enum MetadataRefreshResult
{
    Updated,
    NoMetadata,
    GuardRejected,
    RowGone
}
