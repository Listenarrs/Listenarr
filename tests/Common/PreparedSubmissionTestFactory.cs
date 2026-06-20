namespace Listenarr.Tests.Common;

public static class PreparedSubmissionTestFactory
{
    public static PreparedTorrentSubmission Torrent(
        SearchResult result,
        byte[]? bytes = null,
        string? magnet = null)
    {
        var candidate = TrustedDownloadCandidateFactory.Create(result);
        return new TorrentMetadataService().Prepare(
            candidate,
            bytes ?? result.TorrentFileContent,
            magnet ?? result.MagnetLink,
            result.TorrentUrl ?? result.MagnetLink ?? result.Id,
            result.TorrentFileName);
    }

    public static PreparedTorrentSubmission Torrent(
        string title,
        string infoHash,
        byte[]? bytes = null,
        string? magnet = null)
        => new(
            title,
            string.Empty,
            string.Empty,
            "Test",
            null,
            null,
            0,
            magnet ?? "test.torrent",
            infoHash,
            bytes,
            magnet,
            "test.torrent",
            []);
}
