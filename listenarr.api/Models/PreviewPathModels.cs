namespace Listenarr.Api.Models
{
    public class PreviewPathRequest
    {
        public AudibleBookMetadata Metadata { get; set; } = new();
        public string? DestinationRoot { get; set; }
    }

    public sealed record PreviewPathResponse(string FullPath, string RelativePath, string Root);
}
