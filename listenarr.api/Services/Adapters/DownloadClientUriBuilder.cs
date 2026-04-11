using Listenarr.Domain.Models.Configurations;

namespace Listenarr.Api.Services.Adapters
{
    internal readonly record struct TorrentAddTarget(string Value, bool IsMagnet);

    internal static class DownloadClientUriBuilder
    {
        public static string BuildAuthority(DownloadClientConfiguration client)
        {
            return BuildUri(client, "/").GetLeftPart(UriPartial.Authority);
        }

        public static TorrentAddTarget ResolveTorrentAddTarget(SearchResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var magnetLink = NormalizeMagnetLink(result.MagnetLink);
            if (!string.IsNullOrEmpty(magnetLink))
            {
                return new TorrentAddTarget(magnetLink, true);
            }

            var torrentUrl = (result.TorrentUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(torrentUrl))
            {
                throw new ArgumentException("No magnet link or torrent URL provided", nameof(result));
            }

            if (!TryParseHttpOrHttpsAbsoluteUri(torrentUrl, out var torrentUri))
            {
                throw new ArgumentException("Torrent URL must be an absolute HTTP or HTTPS URL.", nameof(result));
            }

            return new TorrentAddTarget(torrentUri!.ToString(), false);
        }

        public static string NormalizeMagnetLink(string? magnetLink)
        {
            var trimmed = (magnetLink ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            if (!trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Magnet link must use the magnet scheme.", nameof(magnetLink));
            }

            return trimmed;
        }

        public static bool TryParseHttpOrHttpsAbsoluteUri(string? value, out Uri? uri)
        {
            uri = null;
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        public static Uri BuildUri(
            DownloadClientConfiguration client,
            string path,
            bool includeCredentials = false)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            var rawHost = (client.Host ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rawHost))
            {
                throw new InvalidOperationException("Download client host is required.");
            }

            var scheme = client.UseSSL ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            var hostWithScheme = rawHost.Contains("://", StringComparison.Ordinal)
                ? rawHost
                : $"{scheme}://{rawHost}";

            if (!Uri.TryCreate(hostWithScheme, UriKind.Absolute, out var parsedHost) || string.IsNullOrWhiteSpace(parsedHost.Host))
            {
                throw new InvalidOperationException($"Invalid download client host '{rawHost}'.");
            }

            var builder = new UriBuilder(parsedHost)
            {
                Scheme = scheme,
                Port = client.Port > 0 ? client.Port : (parsedHost.IsDefaultPort ? -1 : parsedHost.Port),
                Path = NormalizePath(path),
                Query = string.Empty,
                Fragment = string.Empty
            };

            if (includeCredentials
                && !string.IsNullOrWhiteSpace(client.Username)
                && !string.IsNullOrWhiteSpace(client.Password))
            {
                builder.UserName = client.Username;
                builder.Password = client.Password;
            }
            else
            {
                builder.UserName = string.Empty;
                builder.Password = string.Empty;
            }

            return builder.Uri;
        }

        private static string NormalizePath(string path)
        {
            var trimmed = (path ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return "/";
            }

            return trimmed.StartsWith("/", StringComparison.Ordinal)
                ? trimmed
                : "/" + trimmed;
        }
    }
}
