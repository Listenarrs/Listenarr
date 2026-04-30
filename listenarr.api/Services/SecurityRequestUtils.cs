/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Api.Services;

public static class SecurityRequestUtils
{
    public static bool IsLoopbackRequest(HttpContext? context)
    {
        var ip = context?.Connection?.RemoteIpAddress;
        if (ip == null)
        {
            // TestServer and some internal calls may not populate RemoteIpAddress.
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return IPAddress.IsLoopback(ip);
    }

    public static bool IsLocalOrPrivateRequest(HttpContext? context)
    {
        var ip = context?.Connection?.RemoteIpAddress;
        if (ip == null)
        {
            // TestServer and some internal calls may not populate RemoteIpAddress.
            return true;
        }

        return IsPrivateOrLoopback(ip);
    }

    public static bool IsAuthenticatedAdminOrApiKey(HttpContext? context)
    {
        var user = context?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (user.IsInRole("Administrator"))
        {
            return true;
        }

        var authMethod = user.FindFirst("AuthMethod")?.Value;
        if (!string.IsNullOrWhiteSpace(authMethod) &&
            string.Equals(authMethod, "ApiKey", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldRedactSecretsForCaller(HttpContext? context)
        // *Arr standard trust model:
        // - trusted local/private-network callers may receive non-redacted config payloads
        // - public-network callers must authenticate as admin/API-key to receive secrets
        => !IsLocalOrPrivateRequest(context) && !IsAuthenticatedAdminOrApiKey(context);

    public static string HashSecretForLog(string? secret, string prefix = "sha256")
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return $"{prefix}:empty";
        }

        try
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(secret));
            var hex = Convert.ToHexString(bytes);
            return $"{prefix}:{hex[..12]}";
        }
        catch (CryptographicException)
        {
            return $"{prefix}:error";
        }
    }

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            if (b.Length > 0 && (b[0] & 0xFE) == 0xFC) return true; // fc00::/7
            return false;
        }

        return false;
    }
}
