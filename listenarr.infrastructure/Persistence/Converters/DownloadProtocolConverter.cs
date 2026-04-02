using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Listenarr.Infrastructure.Persistence.Converters
{
    public class DownloadProtocolConverter : ValueConverter<DownloadProtocol, string>
    {
        public DownloadProtocolConverter() : base(
            v => v.ToString(),
            v => ConvertStringToDownloadProtocol(v)
        )
        {
        }

        public static DownloadProtocol ConvertStringToDownloadProtocol(string value)
        {
            if (Enum.TryParse<DownloadProtocol>(value, out DownloadProtocol result))
            {
                return result;
            }

            // Fallback value
            return DownloadProtocol.Torrent;
        }
    }
}