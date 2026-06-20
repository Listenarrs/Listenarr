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

namespace Listenarr.Application.Audiobooks.Files
{
    public sealed class AudiobookFilesystemDeleteResult
    {
        public int DeletedFiles { get; set; }
        public bool DeletedFolder { get; set; }
        public bool DeletedParentFolder { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        public string BuildDeleteMessage()
        {
            var cleanupParts = new List<string>();
            if (DeletedFiles > 0)
            {
                cleanupParts.Add($"removed {DeletedFiles} file{(DeletedFiles == 1 ? string.Empty : "s")}");
            }

            if (DeletedFolder)
            {
                cleanupParts.Add("deleted the audiobook folder");
            }

            if (DeletedParentFolder)
            {
                cleanupParts.Add("deleted the empty author folder");
            }

            var message = cleanupParts.Count > 0
                ? $"Audiobook deleted and {string.Join(" and ", cleanupParts)}."
                : "Audiobook deleted successfully.";

            if (Warnings.Count > 0)
            {
                message += " Some filesystem cleanup steps were skipped.";
            }

            return message;
        }
    }
}
