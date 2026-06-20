/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Text.RegularExpressions;

namespace Listenarr.Domain.Common
{
    public static partial class FileUtils
    {
        public static string NormalizeComparisonValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = Regex.Replace(value, @"[^\p{L}\p{Nd}]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized.ToLowerInvariant();
        }

        public static bool ValuesOverlap(string? left, string? right)
        {
            var normalizedLeft = NormalizeComparisonValue(left);
            var normalizedRight = NormalizeComparisonValue(right);
            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            {
                return false;
            }

            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
                || normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase)
                || normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase);
        }

        public static string ExtractComparableAudioStem(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            name = Regex.Replace(name, @"^(track|chapter|disc|cd|part|pt)\s*\d+[\s\-_\.]*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^\d+[\s\-_\.]*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"[\s\-_]*(part|track|chapter|disc|cd|pt)\s*\d+$", "", RegexOptions.IgnoreCase);

            var normalized = NormalizeComparisonValue(name);
            return IsGenericTrackLabel(normalized) ? string.Empty : normalized;
        }

        public static AudioMatchProfile CreateAudioMatchProfile(string filePath, AudioMetadata? metadata)
        {
            var titleKey = NormalizeComparisonValue(metadata?.Title);
            if (IsGenericTrackLabel(titleKey))
            {
                titleKey = string.Empty;
            }

            var albumKey = NormalizeComparisonValue(metadata?.Album);
            var artistKey = NormalizeComparisonValue(FirstNonEmpty(metadata?.Artist, metadata?.AlbumArtist));
            var stemKey = ExtractComparableAudioStem(filePath);

            return new AudioMatchProfile(filePath, stemKey, titleKey, albumKey, artistKey);
        }

        public static bool LikelyMatchesAnyReference(AudioMatchProfile candidate, IReadOnlyCollection<AudioMatchProfile> references)
        {
            foreach (var reference in references)
            {
                var artistConflict = !string.IsNullOrWhiteSpace(candidate.ArtistKey)
                    && !string.IsNullOrWhiteSpace(reference.ArtistKey)
                    && !ValuesOverlap(candidate.ArtistKey, reference.ArtistKey);
                if (artistConflict)
                {
                    continue;
                }

                var identityMatch =
                    ValuesOverlap(candidate.AlbumKey, reference.AlbumKey)
                    || ValuesOverlap(candidate.AlbumKey, reference.TitleKey)
                    || ValuesOverlap(candidate.AlbumKey, reference.StemKey)
                    || ValuesOverlap(candidate.TitleKey, reference.AlbumKey)
                    || ValuesOverlap(candidate.TitleKey, reference.TitleKey)
                    || ValuesOverlap(candidate.TitleKey, reference.StemKey)
                    || ValuesOverlap(candidate.StemKey, reference.AlbumKey)
                    || ValuesOverlap(candidate.StemKey, reference.TitleKey)
                    || ValuesOverlap(candidate.StemKey, reference.StemKey);

                if (identityMatch)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsLikelyRelatedCompanionFile(
            string filePath,
            IReadOnlyCollection<AudioMatchProfile> references,
            IReadOnlyCollection<string> referenceDirectories)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            if (references == null || references.Count == 0)
            {
                return true;
            }

            var candidate = CreateAudioMatchProfile(filePath, null);
            if (LikelyMatchesAnyReference(candidate, references))
            {
                return true;
            }

            var candidateDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
            var sharesReferenceDirectory = referenceDirectories.Any(directory =>
                !string.IsNullOrWhiteSpace(directory)
                && (string.Equals(
                        NormalizeStoredPath(candidateDirectory),
                        NormalizeStoredPath(directory),
                        StringComparison.OrdinalIgnoreCase)
                    || IsPathInsideOf(candidateDirectory, directory)
                    || IsPathInsideOf(directory, candidateDirectory)));

            if (!sharesReferenceDirectory)
            {
                return false;
            }

            var genericStem = NormalizeComparisonValue(Path.GetFileNameWithoutExtension(filePath));
            return GenericCompanionStemKeys.Contains(genericStem);
        }

        public static int ScoreAgainstTarget(AudioMatchProfile candidate, string? targetTitle, string? targetAlbum, string? targetArtist)
        {
            var score = 0;
            if (ValuesOverlap(candidate.AlbumKey, targetTitle)
                || ValuesOverlap(candidate.TitleKey, targetTitle)
                || ValuesOverlap(candidate.StemKey, targetTitle))
            {
                score += 3;
            }

            if (ValuesOverlap(candidate.AlbumKey, targetAlbum)
                || ValuesOverlap(candidate.TitleKey, targetAlbum)
                || ValuesOverlap(candidate.StemKey, targetAlbum))
            {
                score += 2;
            }

            if (ValuesOverlap(candidate.ArtistKey, targetArtist))
            {
                score += 1;
            }

            return score;
        }
    }
}
