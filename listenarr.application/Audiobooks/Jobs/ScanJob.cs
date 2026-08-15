using System.Text.Json.Serialization;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Jobs
{
    public class ScanJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int AudiobookId { get; set; }
        public string? Path { get; set; }
        public PathIdentitySnapshot? PathIdentity { get; set; }
        [JsonIgnore]
        public ScanPathPhysicalIdentity? PhysicalIdentity { get; set; }
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "Queued";
        public string? Error { get; set; }
        public string? CorrelationId { get; set; }
        public string? DownloadId { get; set; }
        public Guid? MoveScanHandoffId { get; set; }
        public int MoveScanAttemptGeneration { get; set; }
        public bool IsAuthoritativeScope { get; set; } = true;
        [JsonIgnore]
        public ScanAuthorizationMode AuthorizationMode { get; set; } =
            ScanAuthorizationMode.ResolveCurrentAudiobookPath;
    }
}
