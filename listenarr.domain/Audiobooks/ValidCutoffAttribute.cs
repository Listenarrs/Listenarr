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

using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Audiobooks
{
    /// <summary>
    /// Requires a quality profile's cutoff to name one of the profile's own qualities,
    /// and for that quality to be allowed, whenever the profile allows upgrades.
    /// </summary>
    /// <remarks>
    /// This runs only where DataAnnotations run: model binding of an inbound request body.
    /// Reading a profile back out of the database does not go through it, so a profile that
    /// was stored before this rule existed still loads and still serialises.
    ///
    /// The rule is skipped when <see cref="QualityProfile.UpgradeAllowed"/> is false. Readarr and
    /// Sonarr apply theirs unconditionally (src/Readarr.Api.V1/Profiles/Quality/QualityProfileController.cs:25,
    /// src/Sonarr.Api.V3/Profiles/Quality/QualityProfileController.cs:25) but can afford to,
    /// because their cutoff is a non-nullable quality id that always resolves. Listenarr's is a
    /// nullable string whose blank value is how every profile saved before this branch recorded
    /// "upgrades off", and those profiles have to keep round-tripping through PUT.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ValidCutoffAttribute : ValidationAttribute
    {
        public const string CutoffMessage = "Cutoff must be an allowed quality or group";

        public ValidCutoffAttribute()
            : base(CutoffMessage)
        {
        }

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            if (validationContext.ObjectInstance is not QualityProfile profile)
            {
                return ValidationResult.Success;
            }

            if (!profile.UpgradeAllowed)
            {
                return ValidationResult.Success;
            }

            return IsAllowedCutoff(value as string, profile.Qualities)
                ? ValidationResult.Success
                : new ValidationResult(
                    FormatErrorMessage(validationContext.DisplayName),
                    [validationContext.MemberName ?? nameof(QualityProfile.CutoffQuality)]);
        }

        /// <summary>
        /// A cutoff is valid when it names a quality the profile carries and that quality is allowed.
        /// </summary>
        /// <remarks>
        /// Readarr and Sonarr express the same rule as
        /// <c>return cutoffItem is { Allowed: true };</c>
        /// (src/Readarr.Api.V1/Profiles/Quality/QualityCutoffValidator.cs:28 and
        /// src/Sonarr.Api.V3/Profiles/Quality/QualityCutoffValidator.cs:28). Two deliberate
        /// differences, both so that this agrees with the code that later consumes the value:
        ///
        /// The comparison is OrdinalIgnoreCase, matching QualityMatcher.FindAllowedRung, which
        /// resolves a stored cutoff with
        /// <c>AllowedQualities(profile).FirstOrDefault(q =&gt; string.Equals(q.Quality, label,
        /// StringComparison.OrdinalIgnoreCase))</c>. An ordinal comparison here would refuse a
        /// cutoff the matcher resolves happily, so a profile that works today would stop saving.
        ///
        /// The family selects with SingleOrDefault, which throws when a profile carries the same
        /// rung twice. Listenarr stores its rungs as a JSON list with no uniqueness constraint, so
        /// any allowed match counts, which keeps a malformed payload a 400 rather than a 500 and
        /// again matches the matcher, which takes the first allowed rung.
        /// </remarks>
        public static bool IsAllowedCutoff(string? cutoffQuality, IEnumerable<QualityDefinition>? qualities)
        {
            if (string.IsNullOrWhiteSpace(cutoffQuality) || qualities is null)
            {
                return false;
            }

            return qualities.Any(quality =>
                quality.Allowed
                && !string.IsNullOrWhiteSpace(quality.Quality)
                && string.Equals(quality.Quality, cutoffQuality, StringComparison.OrdinalIgnoreCase));
        }
    }
}
