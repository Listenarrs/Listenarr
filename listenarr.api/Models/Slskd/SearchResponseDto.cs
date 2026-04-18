namespace Listenarr.Api.Models.Slskd
{
    public class SearchResponseDto
    {
        public string Username { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public List<FileDto> Files { get; set; } = [];
        public bool HasFreeUploadSlot { get; set; }
        public int LockedFileCount { get; set; }
        public List<FileDto> LockedFiles { get; set; } = [];
        public int QueueLength { get; set; }
        public long Token { get; set; }
        public long UploadSpeed { get; set; }
    }

    public class FileDto
    {
        public string Filename { get; set; } = string.Empty;
        public long Size { get; set; }
        public int? BitRate { get; set; }
        public int? Length { get; set; }
        public string Extension { get; set; } = string.Empty;
        public bool IsLocked { get; set; }
        public int Code { get; set; }
    }
}