using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Common
{
    public class NamingPatternService : INamingPatternService
    {
        private static readonly HashSet<char> PortableInvalidFileNameChars = BuildPortableInvalidFileNameChars();
        private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public string ApplyNamingPattern(
            string pattern,
            Dictionary<string, object> variables,
            bool treatAsFilename = false)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return string.Empty;
            }

            var result = pattern;
            var variableRegex = new Regex(@"\{(\w+)(?::([^}]+))?\}", RegexOptions.IgnoreCase);

            const string emptySentinel = "__EMPTY_VAR__";
            result = variableRegex.Replace(result, match =>
            {
                var variableName = match.Groups[1].Value;
                var format = match.Groups[2].Success ? match.Groups[2].Value : null;

                if (!variables.TryGetValue(variableName, out var value))
                {
                    return emptySentinel;
                }

                if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                {
                    return emptySentinel;
                }

                string renderedValue;
                if (!string.IsNullOrEmpty(format))
                {
                    if (value is int intValue)
                    {
                        renderedValue = intValue.ToString(format);
                    }
                    else if (int.TryParse(value.ToString(), out var parsedInt))
                    {
                        renderedValue = parsedInt.ToString(format);
                    }
                    else
                    {
                        renderedValue = value.ToString() ?? string.Empty;
                    }
                }
                else
                {
                    renderedValue = value.ToString() ?? string.Empty;
                }

                return SanitizePathComponent(renderedValue);
            });

            result = Regex.Replace(result, @"[\(\[\{]\s*" + emptySentinel + @"\s*[\)\]\}]", string.Empty);
            result = Regex.Replace(result, @"\s*[-–—:_]\s*" + emptySentinel, string.Empty);
            result = Regex.Replace(result, emptySentinel + @"\s*[-–—:_]\s*", string.Empty);
            result = Regex.Replace(result, @"/?" + emptySentinel + @"/?", "/");
            result = result.Replace(emptySentinel, string.Empty);
            result = Regex.Replace(result, @"[\\/]{2,}", "/");
            result = Regex.Replace(result, @"\s{2,}", " ");

            if (treatAsFilename)
            {
                var partsForFilename = result.Split(
                    new[] { '/', '\\' },
                    StringSplitOptions.RemoveEmptyEntries);
                result = partsForFilename.Length > 0 ? partsForFilename.Last().Trim() : result.Trim();
                result = result.Replace("/", string.Empty).Replace("\\", string.Empty);
                return SanitizePathComponent(result);
            }

            var parts = result.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            for (int i = parts.Count - 1; i > 0; i--)
            {
                if (string.Equals(parts[i], parts[i - 1], StringComparison.OrdinalIgnoreCase))
                {
                    parts.RemoveAt(i);
                }
            }

            return string.Join(
                Path.DirectorySeparatorChar.ToString(),
                parts.Select(SanitizePathComponent));
        }

        public string ApplyAudiobookNamingPattern(
            string pattern,
            Audiobook audiobook,
            bool treatAsFilename = false)
        {
            return ApplyNamingPattern(pattern, BuildAudiobookVariables(audiobook), treatAsFilename);
        }

        private Dictionary<string, object> BuildAudiobookVariables(Audiobook audiobook)
        {
            return new Dictionary<string, object>
            {
                { "Author", audiobook.Authors?.FirstOrDefault() ?? "Unknown Author" },
                { "Series", audiobook.Series ?? string.Empty },
                { "Title", audiobook.Title ?? "Unknown Title" },
                { "Subtitle", audiobook.Subtitle ?? string.Empty },
                { "Edition", audiobook.Edition ?? string.Empty },
                { "Narrator", audiobook.Narrators != null && audiobook.Narrators.Any()
                    ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n)))
                    : string.Empty },
                { "Publisher", audiobook.Publisher ?? string.Empty },
                { "Language", audiobook.Language ?? string.Empty },
                { "Asin", audiobook.Asin ?? string.Empty },
                { "SeriesNumber", audiobook.SeriesNumber ?? string.Empty },
                { "Year", audiobook.PublishYear ?? string.Empty },
                { "Quality", string.Empty },
                { "DiskNumber", string.Empty },
                { "ChapterNumber", string.Empty }
            };
        }

        private string SanitizePathComponent(string pathComponent)
        {
            if (string.IsNullOrWhiteSpace(pathComponent))
            {
                return "Unknown";
            }

            var sanitized = new StringBuilder();
            foreach (var c in pathComponent)
            {
                if (char.IsControl(c))
                {
                    continue;
                }

                if (c == ':' || c == '/' || c == '\\')
                {
                    sanitized.Append(" - ");
                }
                else if (PortableInvalidFileNameChars.Contains(c))
                {
                    sanitized.Append('_');
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            var result = sanitized.ToString();
            result = Regex.Replace(result, @"\s+", " ");
            result = Regex.Replace(result, @"(?:\s*-\s*){2,}", " - ");
            result = Regex.Replace(result, @"_+", "_");
            result = result.Trim();
            result = result.TrimEnd('.', ' ');
            result = Regex.Replace(result, @"^\s*[-_]+\s*", string.Empty);
            result = Regex.Replace(result, @"\s*[-_]+\s*$", string.Empty);

            if (string.IsNullOrWhiteSpace(result))
            {
                return "Unknown";
            }

            var extensionSeparator = result.IndexOf('.');
            var deviceNameStem = extensionSeparator >= 0 ? result[..extensionSeparator] : result;
            if (ReservedWindowsDeviceNames.Contains(deviceNameStem))
            {
                result = extensionSeparator >= 0
                    ? deviceNameStem + "_" + result[extensionSeparator..]
                    : result + "_";
            }

            return result;
        }

        private static HashSet<char> BuildPortableInvalidFileNameChars()
        {
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());

            foreach (var c in "<>:\"/\\|?*")
            {
                invalidChars.Add(c);
            }

            for (int i = 0; i < 32; i++)
            {
                invalidChars.Add((char)i);
            }

            return invalidChars;
        }
    }
}
