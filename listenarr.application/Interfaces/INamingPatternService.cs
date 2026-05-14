using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    public interface INamingPatternService
    {
        string ApplyNamingPattern(
            string pattern,
            Dictionary<string, object> variables,
            bool treatAsFilename = false);

        string ApplyAudiobookNamingPattern(
            string pattern,
            Audiobook audiobook,
            bool treatAsFilename = false);
    }
}
