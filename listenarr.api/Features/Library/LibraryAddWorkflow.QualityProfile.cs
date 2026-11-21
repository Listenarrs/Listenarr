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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryAddWorkflow
    {
        private async Task<IActionResult?> AssignQualityProfileAsync(Audiobook audiobook, LibraryController.AddToLibraryRequest request)
        {
            using var scope = _scopeFactory.CreateScope();
            var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();

            if (request.QualityProfileId.HasValue)
            {
                var profile = await qualityProfileService.GetByIdAsync(request.QualityProfileId.Value);
                if (profile == null)
                {
                    _logger.LogWarning("Rejected library add for '{Title}': quality profile {ProfileId} does not exist.",
                        LogRedaction.SanitizeText(audiobook.Title), request.QualityProfileId.Value);
                    return DestinationValidationResult(
                        LibraryAddValidationCodes.QualityProfileInvalid,
                        $"Quality profile with ID {request.QualityProfileId.Value} does not exist. Select a valid quality profile.",
                        field: "qualityProfileId");
                }

                audiobook.QualityProfileId = profile.Id;
                _logger.LogInformation("Assigned custom quality profile ID {ProfileId} to new audiobook '{Title}'",
                    profile.Id, LogRedaction.SanitizeText(audiobook.Title));
                return null;
            }

            var defaultProfile = await qualityProfileService.GetDefaultAsync();
            if (defaultProfile == null)
            {
                _logger.LogWarning("Rejected library add for '{Title}': no quality profile supplied and no default profile is configured.",
                    LogRedaction.SanitizeText(audiobook.Title));
                return DestinationValidationResult(
                    LibraryAddValidationCodes.QualityProfileRequired,
                    "A quality profile is required. Select a quality profile, or create one in Settings > Quality Profiles.",
                    field: "qualityProfileId");
            }

            audiobook.QualityProfileId = defaultProfile.Id;
            _logger.LogInformation("Assigned default quality profile '{ProfileName}' (ID: {ProfileId}) to new audiobook '{Title}'",
                defaultProfile.Name, defaultProfile.Id, audiobook.Title);
            return null;
        }
    }
}
