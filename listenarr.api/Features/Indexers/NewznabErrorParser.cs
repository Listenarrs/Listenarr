/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Xml;
using System.Xml.Linq;

namespace Listenarr.Api.Features.Indexers
{
    internal static class NewznabErrorParser
    {
        public static string? Parse(string xmlContent)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null
                };

                using var reader = XmlReader.Create(new StringReader(xmlContent), settings);
                var doc = XDocument.Load(reader);

                var errorElement = doc.Root?.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase) == true
                    ? doc.Root
                    : doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase));

                if (errorElement == null)
                {
                    return null;
                }

                var code = errorElement.Attribute("code")?.Value;
                var description = errorElement.Attribute("description")?.Value ?? errorElement.Value;
                return string.IsNullOrEmpty(description) ? $"Error code: {code}" : description;
            }
            catch (Exception ex) when (ex is not OperationCanceledException &&
                                       ex is not OutOfMemoryException &&
                                       ex is not StackOverflowException)
            {
                return null;
            }
        }
    }
}
