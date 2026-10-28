/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Listenarr.Infrastructure.Search.Providers.InternetArchive;

internal static partial class InternetArchiveRepresentationPlanner
{
    private const int MaxBatchArtifacts = 500;

    public static InternetArchiveItemPlan Create(
        string metadataJson,
        string identifier,
        string title,
        bool allowArchives)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;
        var files = ParseFiles(root);
        var representations = new List<InternetArchiveRepresentation>();
        var issues = new List<InternetArchivePlanIssue>();

        AddM4bRepresentation(files, identifier, representations);
        AddGroupedRepresentation(files, identifier, ArchiveEncoding.Mp3_128, allowArchives, representations, issues);
        AddGroupedRepresentation(files, identifier, ArchiveEncoding.Mp3Vbr, allowArchives, representations, issues);
        AddGroupedRepresentation(files, identifier, ArchiveEncoding.Mp3_64, allowArchives, representations, issues);
        AddGroupedRepresentation(files, identifier, ArchiveEncoding.OggVorbis, allowArchives, representations, issues);

        return new InternetArchiveItemPlan(
            ResolveLanguage(root, title),
            representations,
            issues);
    }

    private static List<ArchiveFile> ParseFiles(JsonElement root)
    {
        if (!root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var files = new List<ArchiveFile>();
        foreach (var element in filesElement.EnumerateArray())
        {
            var name = ReadString(element, "name");
            var format = ReadString(element, "format");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(format))
            {
                continue;
            }

            if (TryClassify(name, format, out var encoding, out var packaging))
            {
                files.Add(new ArchiveFile(name, ReadSize(element), encoding, packaging));
            }
        }

        return files;
    }

    private static void AddM4bRepresentation(
        IReadOnlyList<ArchiveFile> files,
        string identifier,
        ICollection<InternetArchiveRepresentation> results)
    {
        var file = files
            .Where(candidate => candidate.Encoding == ArchiveEncoding.M4b)
            .OrderByDescending(candidate => candidate.Size)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (file == null)
        {
            return;
        }

        results.Add(new InternetArchiveRepresentation(
            "M4B",
            "M4B",
            file.Size,
            1,
            [CreateArtifact(identifier, file)]));
    }

    private static void AddGroupedRepresentation(
        IReadOnlyList<ArchiveFile> files,
        string identifier,
        ArchiveEncoding encoding,
        bool allowArchives,
        ICollection<InternetArchiveRepresentation> results,
        ICollection<InternetArchivePlanIssue> issues)
    {
        var tracks = files
            .Where(file => file.Encoding == encoding && file.Packaging == DirectDownloadArtifactPackaging.File)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var archive = allowArchives
            ? files
                .Where(file => file.Encoding == encoding && file.Packaging == DirectDownloadArtifactPackaging.Archive)
                .OrderByDescending(file => file.Size)
                .FirstOrDefault()
            : null;

        if (archive == null && tracks.Count == 0)
        {
            return;
        }

        if (archive == null && tracks.Count > MaxBatchArtifacts)
        {
            issues.Add(new InternetArchivePlanIssue(GetFormat(encoding), $"exceeds the {MaxBatchArtifacts}-file batch limit"));
            return;
        }

        var artifacts = archive != null
            ? new[] { CreateArtifact(identifier, archive) }
            : tracks.Select(file => CreateArtifact(identifier, file)).ToArray();
        var size = archive?.Size > 0
            ? archive.Size
            : tracks.Sum(file => file.Size);

        results.Add(new InternetArchiveRepresentation(
            GetFormat(encoding),
            GetQuality(encoding),
            size,
            tracks.Count > 0 ? tracks.Count : 1,
            artifacts));
    }

    private static DirectDownloadArtifactDescriptor CreateArtifact(string identifier, ArchiveFile file)
    {
        var escapedIdentifier = Uri.EscapeDataString(identifier);
        var escapedName = EscapeArchivePath(file.Name);
        return new DirectDownloadArtifactDescriptor(
            $"https://archive.org/download/{escapedIdentifier}/{escapedName}",
            Path.GetFileName(file.Name),
            file.Size,
            file.Packaging);
    }

    private static string EscapeArchivePath(string value) =>
        string.Join(
            "/",
            value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

    private static bool TryClassify(
        string name,
        string format,
        out ArchiveEncoding encoding,
        out DirectDownloadArtifactPackaging packaging)
    {
        var extension = Path.GetExtension(name);
        packaging = string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
            ? DirectDownloadArtifactPackaging.Archive
            : DirectDownloadArtifactPackaging.File;
        var normalizedFormat = format.ToLowerInvariant();

        if (string.Equals(extension, ".m4b", StringComparison.OrdinalIgnoreCase) || normalizedFormat.Contains("apple audiobook") || normalizedFormat == "m4b")
        {
            encoding = ArchiveEncoding.M4b;
            packaging = DirectDownloadArtifactPackaging.File;
            return true;
        }

        if (packaging == DirectDownloadArtifactPackaging.File && !IsExpectedTrackExtension(extension, normalizedFormat))
        {
            encoding = default;
            return false;
        }

        if (normalizedFormat.Contains("128") && normalizedFormat.Contains("mp3"))
        {
            encoding = ArchiveEncoding.Mp3_128;
            return true;
        }
        if (normalizedFormat.Contains("vbr") && (normalizedFormat.Contains("mp3") || packaging == DirectDownloadArtifactPackaging.Archive))
        {
            encoding = ArchiveEncoding.Mp3Vbr;
            return true;
        }
        if (normalizedFormat.Contains("64") && normalizedFormat.Contains("mp3"))
        {
            encoding = ArchiveEncoding.Mp3_64;
            return true;
        }
        if (normalizedFormat.Contains("ogg"))
        {
            encoding = ArchiveEncoding.OggVorbis;
            return true;
        }

        encoding = default;
        return false;
    }

    private static bool IsExpectedTrackExtension(string extension, string format) =>
        (string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase) && format.Contains("mp3")) ||
        (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase) && format.Contains("ogg"));

    private static long ReadSize(JsonElement element)
    {
        if (!element.TryGetProperty("size", out var sizeElement))
        {
            return 0;
        }

        return sizeElement.ValueKind switch
        {
            JsonValueKind.Number when sizeElement.TryGetInt64(out var numericSize) => numericSize,
            JsonValueKind.String when long.TryParse(sizeElement.GetString(), out var textSize) => textSize,
            _ => 0
        };
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? ResolveLanguage(JsonElement root, string title)
    {
        if (root.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("language", out var language))
        {
            var values = language.ValueKind switch
            {
                JsonValueKind.String => new[] { language.GetString() },
                JsonValueKind.Array => language.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()),
                _ => []
            };
            var normalized = values
                .Select(NormalizeLanguage)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (normalized != null)
            {
                return normalized;
            }
        }

        var namedMatch = LanguageNameRegex().Match(title);
        return namedMatch.Success
            ? NormalizeLanguage(namedMatch.Value)
            : SearchResultAttributeParser.ParseLanguageFromText(title);
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var parsedCode = SearchResultAttributeParser.ParseLanguageFromCode(trimmed);
        if (parsedCode != null)
        {
            return parsedCode;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "english" => "English",
            "french" or "français" => "French",
            "german" or "deutsch" => "German",
            "spanish" or "español" => "Spanish",
            "dutch" or "nederlands" => "Dutch",
            _ => trimmed
        };
    }

    private static string GetFormat(ArchiveEncoding encoding) => encoding switch
    {
        ArchiveEncoding.Mp3_128 => "128Kbps MP3",
        ArchiveEncoding.Mp3Vbr => "VBR MP3",
        ArchiveEncoding.Mp3_64 => "64Kbps MP3",
        ArchiveEncoding.OggVorbis => "Ogg Vorbis",
        _ => "M4B"
    };

    private static string GetQuality(ArchiveEncoding encoding) => encoding switch
    {
        ArchiveEncoding.Mp3_128 => "MP3 128kbps",
        ArchiveEncoding.Mp3Vbr => "MP3 VBR",
        ArchiveEncoding.Mp3_64 => "MP3 64kbps",
        ArchiveEncoding.OggVorbis => "OGG Vorbis",
        _ => "M4B"
    };

    [GeneratedRegex(@"\b(?:English|French|German|Spanish|Dutch|Français|Deutsch|Español|Nederlands)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageNameRegex();

    private enum ArchiveEncoding
    {
        M4b,
        Mp3_128,
        Mp3Vbr,
        Mp3_64,
        OggVorbis
    }

    private sealed record ArchiveFile(
        string Name,
        long Size,
        ArchiveEncoding Encoding,
        DirectDownloadArtifactPackaging Packaging);
}

internal sealed record InternetArchiveItemPlan(
    string? Language,
    IReadOnlyList<InternetArchiveRepresentation> Representations,
    IReadOnlyList<InternetArchivePlanIssue> Issues);

internal sealed record InternetArchiveRepresentation(
    string Format,
    string Quality,
    long Size,
    int FileCount,
    IReadOnlyList<DirectDownloadArtifactDescriptor> Artifacts);

internal sealed record InternetArchivePlanIssue(string Format, string Reason);
