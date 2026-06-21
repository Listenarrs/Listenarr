/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Common.Exceptions;

public abstract class ListenarrApplicationException : Exception
{
    protected ListenarrApplicationException(string code, string safeDetail, Exception? innerException = null)
        : base(safeDetail, innerException)
    {
        Code = code;
        SafeDetail = safeDetail;
    }

    public string Code { get; }

    public string SafeDetail { get; }
}

public sealed class ApplicationValidationException : ListenarrApplicationException
{
    public ApplicationValidationException(string code, string safeDetail)
        : base(code, safeDetail)
    {
    }
}

public sealed class ApplicationNotFoundException : ListenarrApplicationException
{
    public ApplicationNotFoundException(string code, string safeDetail)
        : base(code, safeDetail)
    {
    }
}

public sealed class ApplicationConflictException : ListenarrApplicationException
{
    public ApplicationConflictException(string code, string safeDetail, Exception? innerException = null)
        : base(code, safeDetail, innerException)
    {
    }
}

public sealed class ApplicationForbiddenException : ListenarrApplicationException
{
    public ApplicationForbiddenException(string code, string safeDetail)
        : base(code, safeDetail)
    {
    }
}

public sealed class ExternalServiceException : ListenarrApplicationException
{
    public ExternalServiceException(string code, string safeDetail, Exception? innerException = null)
        : base(code, safeDetail, innerException)
    {
    }
}
