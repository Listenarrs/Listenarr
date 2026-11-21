using System.ComponentModel.DataAnnotations;

namespace Listenarr.Api.Features.Library;

public sealed class MonitoringQualityProfileRequest
{
    [Range(1, int.MaxValue)]
    public int QualityProfileId { get; set; }
}
