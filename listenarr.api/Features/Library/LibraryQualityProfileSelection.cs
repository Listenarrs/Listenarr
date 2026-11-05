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

using Listenarr.Application.Common.Exceptions;

namespace Listenarr.Api.Features.Library;

internal static class LibraryQualityProfileSelection
{
    private const int UseDefaultProfileId = -1;

    /// <summary>
    /// Resolves a requested quality profile id to an existing profile id. <c>-1</c> means "use the
    /// default profile". Throws <see cref="ApplicationValidationException"/> instead of returning an
    /// unusable id, so a rejected edit never clears or corrupts the current assignment.
    /// </summary>
    public static async Task<int> ResolveAsync(
        IQualityProfileService qualityProfileService,
        int requestedProfileId)
    {
        if (requestedProfileId == UseDefaultProfileId)
        {
            var defaultProfile = await qualityProfileService.GetDefaultAsync();
            return defaultProfile?.Id
                ?? throw new ApplicationValidationException(
                    "quality_profile_default_missing",
                    "No default quality profile is configured.");
        }

        var profile = await qualityProfileService.GetByIdAsync(requestedProfileId);
        return profile?.Id
            ?? throw new ApplicationValidationException(
                "quality_profile_not_found",
                $"Quality profile {requestedProfileId} does not exist.");
    }
}
