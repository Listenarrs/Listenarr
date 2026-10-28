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

namespace Listenarr.Application.Configuration.Contracts
{
    /// <summary>
    /// Provides resolved, fully-qualified paths to well-known application directories.
    /// Implementations must return absolute paths and must not include trailing directory separators.
    /// </summary>
    public interface IApplicationPathService
    {
        /// <summary>Gets the absolute path to the application content root (the directory that contains the binary or the configured override).</summary>
        string ContentRootPath { get; }

        /// <summary>Gets the absolute path to the configuration root directory (<c>ContentRootPath/config</c>).</summary>
        string ConfigRootPath { get; }

        /// <summary>Gets the absolute path to the logs directory (<c>ConfigRootPath/logs</c>).</summary>
        string LogsRootPath { get; }

        /// <summary>
        /// Gets the absolute path to the file-move coordination directory
        /// (<c>ConfigRootPath/runtime/file-move-locks</c>). Processes that share a
        /// Listenarr configuration root share this coordination namespace.
        /// </summary>
        string FileMoveLockRootPath { get; }

        /// <summary>Gets the absolute path to the bundled FFmpeg directory (<c>ConfigRootPath/ffmpeg</c>).</summary>
        string FfmpegRootPath { get; }

        /// <summary>Gets the absolute path to the bundled tools directory (<c>ContentRootPath/tools</c>).</summary>
        string ToolsRootPath { get; }

        /// <summary>Gets the absolute path to the bundled Discord bot directory (<c>ToolsRootPath/discord-bot</c>).</summary>
        string DiscordBotRootPath { get; }

        /// <summary>Gets the absolute path to the static web assets directory (<c>ContentRootPath/wwwroot</c>).</summary>
        string WwwRootPath { get; }

        /// <summary>
        /// Resolves a path by joining one or more relative <paramref name="segments"/> under <see cref="ContentRootPath"/>.
        /// </summary>
        /// <param name="segments">Relative path segments to append. Must not be rooted.</param>
        /// <returns>A normalised, absolute path.</returns>
        string ResolveFromContentRoot(params string[] segments);

        /// <summary>
        /// Resolves a path by joining one or more relative <paramref name="segments"/> under <see cref="ConfigRootPath"/>.
        /// </summary>
        /// <param name="segments">Relative path segments to append. Must not be rooted.</param>
        /// <returns>A normalised, absolute path.</returns>
        string ResolveFromConfig(params string[] segments);
    }
}
