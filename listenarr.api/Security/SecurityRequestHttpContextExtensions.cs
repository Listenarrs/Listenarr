/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;

namespace Listenarr.Api.Security;

public static class HttpSecurityRequestUtils
{
    public static bool IsLoopbackRequest(HttpContext? context)
    {
        var ip = context?.Connection?.RemoteIpAddress;
        if (ip == null)
        {
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
            return true;
        }

        return Listenarr.Application.Security.SecurityRequestUtils.IsPrivateOrLoopback(ip);
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
        return !string.IsNullOrWhiteSpace(authMethod)
               && string.Equals(authMethod, "ApiKey", StringComparison.Ordinal);
    }

    public static bool IsApiKeyAuthenticated(HttpContext? context)
    {
        var user = context?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var authMethod = user.FindFirst("AuthMethod")?.Value;
        return !string.IsNullOrWhiteSpace(authMethod)
               && string.Equals(authMethod, "ApiKey", StringComparison.Ordinal);
    }

    public static bool ShouldRedactSecretsForCaller(HttpContext? context)
        => !IsLocalOrPrivateRequest(context) && !IsAuthenticatedAdminOrApiKey(context);
}
