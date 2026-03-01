using AsyncKeyedLock;

namespace Listenarr.Api.Services
{
    public class MetadataExtractionLimiter
    {
        // Default concurrent ffprobe extractions
        public AsyncNonKeyedLocker Sem { get; } = new(4);
    }
}
