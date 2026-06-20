namespace Listenarr.Domain.Downloads
{
    public class PlannedImportFile
    {
        public string FullPath { get; init; } = string.Empty;
        public string? RelativePath { get; init; }
        public int SequenceNumber { get; init; }
        public int? DiskNumberHint { get; init; }
        public int? ChapterNumberHint { get; init; }
    }
}
