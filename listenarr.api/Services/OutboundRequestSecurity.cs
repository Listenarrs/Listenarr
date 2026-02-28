using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services;

public static class OutboundRequestSecurity
{
    public static bool TryValidateExternalHttpUrl(string url, out string reason, bool allowPrivateTargets = false)
    {
        reason = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "Invalid URL format";
            return false;
        }

        return TryValidateExternalHttpUri(uri, out reason, allowPrivateTargets);
    }

    public static bool TryValidateExternalHttpUri(Uri uri, out string reason, bool allowPrivateTargets = false)
    {
        reason = string.Empty;

        if (!uri.IsAbsoluteUri)
        {
            reason = "URL must be absolute";
            return false;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Unsupported URL scheme '{uri.Scheme}'";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            reason = "URLs with embedded credentials are not allowed";
            return false;
        }

        if (!allowPrivateTargets)
        {
            var host = uri.Host ?? string.Empty;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Localhost or local-network hostnames are not allowed";
                return false;
            }

            if (IPAddress.TryParse(host, out var ip) && SecurityRequestUtils.IsPrivateOrLoopback(ip))
            {
                reason = "Private or loopback IP targets are not allowed";
                return false;
            }
        }

        return true;
    }

    public static async Task<bool> TryValidateResolvedExternalHttpUriAsync(Uri uri, ILogger logger, bool allowPrivateTargets = false)
    {
        if (allowPrivateTargets)
        {
            return true;
        }

        try
        {
            var host = uri.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (IPAddress.TryParse(host, out var parsedIp))
            {
                return !SecurityRequestUtils.IsPrivateOrLoopback(parsedIp);
            }

            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses == null || addresses.Length == 0)
            {
                logger.LogWarning("Blocked outbound request because DNS resolution returned no addresses: {Host}", host);
                return false;
            }

            var privateOrLoopback = addresses.FirstOrDefault(SecurityRequestUtils.IsPrivateOrLoopback);
            if (privateOrLoopback != null)
            {
                logger.LogWarning(
                    "Blocked outbound request because DNS resolved to private/loopback address. Host={Host}, Address={Address}",
                    host,
                    privateOrLoopback);
                return false;
            }

            return true;
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Blocked outbound request because DNS resolution failed for host {Host}", uri.Host);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            logger.LogWarning(ex, "Blocked outbound request due to unexpected DNS validation error for host {Host}", uri.Host);
            return false;
        }
    }

    public static async Task<(HttpResponseMessage Response, Uri FinalUri)> SendWithValidatedRedirectsAsync(
        Func<Uri, HttpRequestMessage> requestFactory,
        Uri initialUri,
        HttpClient noRedirectClient,
        ILogger logger,
        bool allowPrivateTargets = false,
        int maxRedirects = 5,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        if (requestFactory == null) throw new ArgumentNullException(nameof(requestFactory));
        if (noRedirectClient == null) throw new ArgumentNullException(nameof(noRedirectClient));

        Uri currentUri = initialUri;
        HttpResponseMessage? response = null;

        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            if (!TryValidateExternalHttpUri(currentUri, out var uriValidationReason, allowPrivateTargets))
            {
                throw new InvalidOperationException($"Blocked outbound URL: {uriValidationReason}");
            }

            if (!await TryValidateResolvedExternalHttpUriAsync(currentUri, logger, allowPrivateTargets))
            {
                throw new InvalidOperationException("Blocked outbound URL: DNS resolved to private or loopback address");
            }

            response?.Dispose();
            using var request = requestFactory(currentUri);
            response = await noRedirectClient.SendAsync(request, completionOption, cancellationToken);

            if (IsRedirectStatusCode(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location == null)
                {
                    throw new HttpRequestException($"Redirect response from {currentUri} did not include a Location header.");
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!TryValidateExternalHttpUri(nextUri, out var redirectValidationReason, allowPrivateTargets))
                {
                    throw new InvalidOperationException($"Blocked redirect target: {redirectValidationReason}");
                }

                currentUri = nextUri;
                continue;
            }

            var finalUri = response.RequestMessage?.RequestUri ?? currentUri;
            if (!TryValidateExternalHttpUri(finalUri, out var finalValidationReason, allowPrivateTargets))
            {
                throw new InvalidOperationException($"Blocked final outbound URL: {finalValidationReason}");
            }

            if (!await TryValidateResolvedExternalHttpUriAsync(finalUri, logger, allowPrivateTargets))
            {
                throw new InvalidOperationException("Blocked final outbound URL: DNS resolved to private or loopback address");
            }

            return (response, finalUri);
        }

        response?.Dispose();
        throw new HttpRequestException($"Too many redirects while sending outbound request (>{maxRedirects}).");
    }

    public static bool IsRedirectStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.Moved
               || statusCode == HttpStatusCode.Redirect
               || statusCode == HttpStatusCode.RedirectMethod
               || statusCode == HttpStatusCode.TemporaryRedirect
               || (int)statusCode == 308;
    }
}

