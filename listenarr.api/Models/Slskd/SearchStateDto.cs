namespace Listenarr.Api.Models.Slskd
{
    public class SearchStateDto
    {
        public int FileCount { get; set; }
        public required string Id { get; set; }
        public bool IsComplete { get; set; }
        public int LockedFileCount { get; set; }
        public int ResponseCount { get; set; }
        public List<string> Responses { get; set; } = new();
        public string SearchText { get; set; } = string.Empty;
        public string StartedAt { get; set; } = string.Empty;
        public string EndedAt { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int Token { get; set; }
    }
}