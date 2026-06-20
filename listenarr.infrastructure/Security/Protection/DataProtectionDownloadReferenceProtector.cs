/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Infrastructure.Security.Protection;

public sealed class DataProtectionDownloadReferenceProtector : IDownloadReferenceProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionDownloadReferenceProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Listenarr.DownloadReference.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
