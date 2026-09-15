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
namespace Listenarr.Domain.Common
{
    /// <summary>
    /// One spelling of "why did this fail", for every place that has to put an exception into
    /// text a person reads.
    /// </summary>
    /// <remarks>
    /// Two sites grew their own copy of this: the import result that a History row persists, and
    /// the file publication capability gate that refuses a source file. They disagreed, so the
    /// same wrapped failure read as a chain in one record and as a single line in the other. This
    /// is the seam they should have shared. It lives in the domain because the domain references
    /// nothing, so the application and infrastructure projects can both reach it.
    ///
    /// It formats and nothing else. Redaction and truncation belong to the caller, because
    /// <c>LogRedaction</c> is in the application project and the domain cannot see it.
    /// </remarks>
    public static class ExceptionCause
    {
        /// <summary>
        /// The exception's type and message, followed by the same for each inner cause.
        /// </summary>
        /// <param name="exception">The exception to describe. Null yields an empty string.</param>
        /// <param name="maxDepth">
        /// How many frames of the cause chain to walk. The guard matters because an exception can
        /// be constructed with itself as an inner cause.
        /// </param>
        /// <returns>
        /// For example <c>InvalidOperationException: Unable to perform HardlinkCopy -&gt;
        /// IOException: Invalid cross-device link</c>. Repeated frames are collapsed, because a
        /// wrapper that rethrows with the same message says nothing twice.
        /// </returns>
        public static string Describe(Exception? exception, int maxDepth = 8)
        {
            if (exception == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var current = exception;
            var guard = 0;
            while (current != null && guard++ < maxDepth)
            {
                var text = $"{current.GetType().Name}: {current.Message}";
                if (!parts.Contains(text))
                {
                    parts.Add(text);
                }

                current = current.InnerException;
            }

            return string.Join(" -> ", parts);
        }
    }
}
