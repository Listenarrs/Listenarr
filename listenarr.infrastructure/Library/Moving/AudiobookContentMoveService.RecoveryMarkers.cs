using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private void ValidateExistingRecoveryMarker(
        string markerDirectory,
        string markerPath,
        AudiobookContentMoveRequest request,
        string source,
        string target)
    {
        _ = ReadValidatedExistingRecoveryMarker(
            markerDirectory,
            markerPath,
            request,
            source,
            target);
    }

    private void ValidateExistingRecoveryMarkerForStage(
        string markerDirectory,
        string markerPath,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string candidateStage)
    {
        var existing = ReadValidatedExistingRecoveryMarker(
            markerDirectory,
            markerPath,
            request,
            source,
            target);
        if (!CanAdvanceRecoveryStage(existing.Stage, candidateStage))
        {
            throw new MoveNeedsAttentionException(
                "The existing recovery marker is already at a later or incompatible stage.");
        }
    }

    private ParsedRecoveryMarker ReadValidatedExistingRecoveryMarker(
        string markerDirectory,
        string markerPath,
        AudiobookContentMoveRequest request,
        string source,
        string target)
    {
        ValidateExistingMoveDirectory(markerDirectory, "recovery-marker directory");
        if (!FileSystemSafety.TryValidateMutationTarget(
                markerPath,
                [markerDirectory],
                out markerPath,
                out var markerReason))
        {
            throw new MoveNeedsAttentionException(markerReason);
        }

        if (!File.Exists(markerPath)
            || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The existing recovery marker is missing or linked.");
        }

        var parsed = ReadRecoveryMarker(markerPath)
            ?? throw new MoveNeedsAttentionException("The existing recovery marker disappeared.");
        ValidateRecoveryMarker(parsed, request, source, target);
        return parsed;
    }

    private static void ValidateNewRecoveryMarkerWritePath(
        string writePath,
        string markerDirectory)
    {
        ValidateExistingMoveDirectory(markerDirectory, "recovery-marker directory");
        if (!FileSystemSafety.TryValidateMutationTarget(
                writePath,
                [markerDirectory],
                out writePath,
                out var writeReason))
        {
            throw new MoveNeedsAttentionException(writeReason);
        }

        if (File.Exists(writePath) || Directory.Exists(writePath))
        {
            throw new MoveNeedsAttentionException(
                "The recovery-marker temporary path appeared before creation.");
        }
    }

    private static void ValidateRecoveryMarkerPublicationPaths(
        string markerDirectory,
        string writePath,
        string markerPath)
    {
        ValidateExistingMoveDirectory(markerDirectory, "recovery-marker directory");
        ValidateRecoveryMarkerWritePath(writePath, markerDirectory);
        if (!FileSystemSafety.TryValidateMutationTarget(
                markerPath,
                [markerDirectory],
                out markerPath,
                out var markerReason))
        {
            throw new MoveNeedsAttentionException(markerReason);
        }

        if (File.Exists(markerPath)
            && (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The authoritative recovery marker became a symbolic link or reparse point.");
        }
    }

    private static void ValidateRecoveryMarkerWritePath(
        string writePath,
        string markerDirectory)
    {
        ValidateExistingMoveDirectory(markerDirectory, "recovery-marker directory");
        if (!FileSystemSafety.TryValidateMutationTarget(
                writePath,
                [markerDirectory],
                out writePath,
                out var writeReason))
        {
            throw new MoveNeedsAttentionException(writeReason);
        }

        if (!File.Exists(writePath)
            || (File.GetAttributes(writePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The recovery-marker write-temporary file is missing or linked.");
        }
    }

    private ParsedRecoveryMarker? ReadRecoveryMarker(string markerPath)
    {
        if (!File.Exists(markerPath))
        {
            return null;
        }

        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The move recovery marker is a symbolic link or reparse point.");
        }

        try
        {
            var fileInfo = new FileInfo(markerPath);
            if (fileInfo.Length > MaximumMarkerLength)
            {
                throw new MoveNeedsAttentionException(
                    "The move recovery marker exceeds the supported size and was preserved.");
            }

            var content = File.ReadAllText(markerPath).Trim();
            return ParseRecoveryMarkerContent(content);
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Move recovery marker {Marker} is temporarily unreadable",
                LogRedaction.SanitizeFilePath(markerPath));
            throw new IOException(
                "The move recovery marker is temporarily unreadable and was preserved.",
                exception);
        }
    }

    private ParsedRecoveryMarker ReadRecoveryMarker(
        PinnedDirectoryCreation.PinnedFileEntry markerEntry,
        string markerPath)
    {
        try
        {
            using var stream = markerEntry.OpenReadStream(
                bufferSize: 4096,
                asynchronous: false);
            if (stream.Length > MaximumMarkerLength)
            {
                throw new MoveNeedsAttentionException(
                    "The move recovery marker exceeds the supported size and was preserved.");
            }

            stream.Position = 0;
            using var reader = new StreamReader(
                stream,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            return ParseRecoveryMarkerContent(reader.ReadToEnd().Trim());
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Pinned move recovery marker {Marker} is temporarily unreadable",
                LogRedaction.SanitizeFilePath(markerPath));
            throw new IOException(
                "The move recovery marker is temporarily unreadable and was preserved.",
                exception);
        }
    }

    private static ParsedRecoveryMarker ParseRecoveryMarkerContent(string content)
    {
        if (IsKnownRecoveryStage(content))
        {
            return new ParsedRecoveryMarker(
                StructuredMarker: null,
                ObsoleteStage: content);
        }

        MoveRecoveryMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<MoveRecoveryMarker>(content);
        }
        catch (JsonException exception)
        {
            throw new MoveNeedsAttentionException(
                $"The move recovery marker is corrupt or truncated: {exception.Message}");
        }

        if (marker == null)
        {
            throw new MoveNeedsAttentionException(
                "The move recovery marker is empty or corrupt.");
        }

        if (marker.Version != RecoveryMarkerVersion
            || !IsKnownRecoveryStage(marker.Stage))
        {
            throw new MoveNeedsAttentionException(
                "The move recovery marker uses an unsupported version or stage and was preserved.");
        }

        return new ParsedRecoveryMarker(marker, ObsoleteStage: null);
    }

    private static void ValidateRecoveryMarker(
        ParsedRecoveryMarker? parsedMarker,
        AudiobookContentMoveRequest request,
        string source,
        string target)
    {
        if (parsedMarker == null)
        {
            return;
        }

        if (parsedMarker.IsObsolete)
        {
            throw new MoveNeedsAttentionException(
                "This move contains an obsolete pre-release recovery marker and cannot be resumed safely.");
        }

        var marker = parsedMarker.StructuredMarker
            ?? throw new MoveNeedsAttentionException("The move recovery marker is invalid.");
        if (marker.Version != RecoveryMarkerVersion || marker.JobId != request.JobId)
        {
            throw new MoveNeedsAttentionException(
                "Move recovery marker is owned by a different job or unsupported marker version.");
        }

        try
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    marker.Source,
                    out var markerSource,
                    out _,
                    request.SourceSemantics.Syntax)
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    marker.Target,
                    out var markerTarget,
                    out _,
                    request.TargetSemantics.Syntax)
                || !FileSystemPathIdentity.AreEquivalent(
                    markerSource,
                    source,
                    request.SourceSemantics)
                || !FileSystemPathIdentity.AreEquivalent(
                    markerTarget,
                    target,
                    request.TargetSemantics))
            {
                throw new MoveNeedsAttentionException(
                    "Move recovery marker source or target identity does not match the persisted job.");
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                "Move recovery marker contains an invalid source or target identity.");
        }
    }

    private static void ValidateRecoveryMarkerLocation(
        string markerPath,
        string target,
        FileSystemPathSemantics targetSemantics)
    {
        var markerDirectory = Path.GetDirectoryName(Path.GetFullPath(markerPath));
        var reason = string.Empty;
        if (string.IsNullOrWhiteSpace(markerDirectory)
            || !FileSystemPathIdentity.AreEquivalent(markerDirectory, target, targetSemantics)
            || !FileSystemSafety.TryValidateMutationTarget(
                markerPath,
                [target],
                out _,
                out reason))
        {
            throw new MoveNeedsAttentionException(
                string.IsNullOrWhiteSpace(reason)
                    ? "Move recovery marker is not located inside the persisted target directory."
                    : reason);
        }
    }

    private static bool CanAdvanceRecoveryStage(string currentStage, string candidateStage)
    {
        if (string.Equals(currentStage, candidateStage, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(currentStage, AtomicRenameCompletedStage, StringComparison.Ordinal)
            || string.Equals(candidateStage, AtomicRenameCompletedStage, StringComparison.Ordinal))
        {
            return false;
        }

        static int GetOrder(string stage) => stage switch
        {
            CopyStartedStage => 0,
            CopyCompletedStage => 1,
            SourceCleanupCompletedStage => 2,
            _ => -1
        };

        var currentOrder = GetOrder(currentStage);
        var candidateOrder = GetOrder(candidateStage);
        return currentOrder >= 0 && candidateOrder > currentOrder;
    }

    private static bool IsKnownRecoveryStage(string? stage) =>
        stage is CopyStartedStage
            or CopyCompletedStage
            or AtomicRenameCompletedStage
            or SourceCleanupCompletedStage;

    private static string GetRecoveryMarkerPath(string target, Guid jobId) =>
        Path.Join(target, $".listenarr-move-{jobId:N}.pending");
}
