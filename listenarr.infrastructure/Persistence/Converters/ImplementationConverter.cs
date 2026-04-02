using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Listenarr.Infrastructure.Persistence.Converters
{
    public class ImplementationConverter : ValueConverter<Implementation, string>
    {
        public ImplementationConverter() : base(
            v => v.ToString(),
            v => ConvertStringToImplementation(v)
        )
        {
        }

        public static Implementation ConvertStringToImplementation(string value)
        {
            if (Enum.TryParse<Implementation>(value, out Implementation result))
            {
                return result;
            }

            // Fallback value
            return Implementation.Custom;
        }
    }
}