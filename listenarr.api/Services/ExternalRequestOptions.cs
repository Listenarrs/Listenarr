namespace Listenarr.Api.Services
{
    // Options to control external request behavior (US proxy / domain preference)
    public class ExternalRequestOptions
    {
            // When true, attempts to force .com domains when localized content is detected
            public bool PreferUsDomain { get; set; } = true;
    }
}
