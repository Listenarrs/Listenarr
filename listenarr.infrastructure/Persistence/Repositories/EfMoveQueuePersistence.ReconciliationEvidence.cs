using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveQueuePersistence
{
    private static OwnershipEvidenceResult ReadTargetOwnershipEvidence(
        IReadOnlyCollection<(MoveJob Job, string Key, PathIdentitySnapshot TargetIdentity)> candidates)
    {
        var sample = candidates.First();
        var target = sample.Job.RequestedPath!;
        if (!Directory.Exists(target))
        {
            return OwnershipEvidenceResult.None;
        }

        try
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            {
                return OwnershipEvidenceResult.Ambiguous("The move target is a symbolic link or reparse point.");
            }

            var markerPath = Path.Join(target, ".listenarr-temp-owner.json");
            if (File.Exists(markerPath))
            {
                if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return OwnershipEvidenceResult.Ambiguous("The target ownership marker is linked.");
                }

                var markerInfo = new FileInfo(markerPath);
                if (markerInfo.Length <= 0 || markerInfo.Length > MaximumOwnershipMarkerBytes)
                {
                    return OwnershipEvidenceResult.Ambiguous("The target ownership marker has an invalid size.");
                }

                using var stream = new FileStream(
                    markerPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                var marker = JsonSerializer.Deserialize<OwnershipMarkerIdentity>(stream);
                if (marker == null
                    || marker.Version != 1
                    || marker.JobId == Guid.Empty
                    || !string.Equals(marker.ArtifactType, "temporary-directory", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(marker.Source)
                    || string.IsNullOrWhiteSpace(marker.Target)
                    || string.IsNullOrWhiteSpace(marker.DirectoryPath)
                    || marker.OwnedArtifactType != null
                    || marker.OwnedDirectoryPath != null)
                {
                    return OwnershipEvidenceResult.Ambiguous("The target ownership marker is corrupt or unsupported.");
                }

                var owners = candidates
                    .Where(candidate => candidate.Job.Id == marker.JobId)
                    .ToList();
                if (owners.Count != 1
                    || !owners[0].Job.TryGetSourceIdentity(out var sourceIdentity)
                    || string.IsNullOrWhiteSpace(owners[0].Job.SourcePath)
                    || string.IsNullOrWhiteSpace(owners[0].Job.RequestedPath))
                {
                    return OwnershipEvidenceResult.Ambiguous(
                        "The target ownership marker cannot be attributed to one active move identity.");
                }

                var owner = owners[0];
                var sourcePath = owner.Job.SourcePath!;
                var requestedPath = owner.Job.RequestedPath!;
                var markerSource = marker.Source!;
                var markerTarget = marker.Target!;
                var markerDirectoryPath = marker.DirectoryPath!;
                var targetParent = Path.GetDirectoryName(requestedPath);
                if (string.IsNullOrWhiteSpace(targetParent))
                {
                    return OwnershipEvidenceResult.Ambiguous(
                        "The target ownership marker has no valid destination parent.");
                }

                var expectedDirectory = Path.Join(
                    targetParent,
                    Path.GetFileName(requestedPath)
                        + ".tmp-"
                        + owner.Job.Id.ToString("N"));
                if (!FileSystemPathIdentity.AreEquivalent(
                        markerSource,
                        sourcePath,
                        sourceIdentity.Semantics)
                    || !FileSystemPathIdentity.AreEquivalent(
                        markerTarget,
                        requestedPath,
                        owner.TargetIdentity.Semantics)
                    || !FileSystemPathIdentity.AreEquivalent(
                        markerDirectoryPath,
                        expectedDirectory,
                        owner.TargetIdentity.Semantics))
                {
                    return OwnershipEvidenceResult.Ambiguous(
                        "The target ownership marker does not match the persisted source, target, or temporary directory.");
                }

                return OwnershipEvidenceResult.Valid(marker.JobId);
            }

            var writeOwners = new HashSet<Guid>();
            foreach (var writePath in Directory.EnumerateFiles(
                target,
                ".listenarr-temp-owner.json.writing-*",
                SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(writePath) & FileAttributes.ReparsePoint) != 0)
                {
                    return OwnershipEvidenceResult.Ambiguous("A target ownership-marker write file is linked.");
                }

                var fileName = Path.GetFileName(writePath);
                var owners = candidates
                    .Where(candidate => fileName.Contains(
                        $"writing-{candidate.Job.Id:N}-",
                        StringComparison.Ordinal))
                    .Select(candidate => candidate.Job.Id)
                    .ToList();
                if (owners.Count != 1)
                {
                    return OwnershipEvidenceResult.Ambiguous(
                        "A target ownership-marker write file cannot be attributed to one active move job.");
                }

                writeOwners.Add(owners[0]);
            }

            return writeOwners.Count switch
            {
                0 => OwnershipEvidenceResult.None,
                1 => OwnershipEvidenceResult.Valid(writeOwners.Single()),
                _ => OwnershipEvidenceResult.Ambiguous(
                    "Multiple move jobs have incomplete target ownership-marker publications.")
            };
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException or JsonException)
        {
            return OwnershipEvidenceResult.Ambiguous(
                $"Target ownership evidence is unreadable: {exception.Message}");
        }
    }

    private static JobEvidenceState CollectJobSpecificRecoveryEvidence(
        MoveJob job,
        IReadOnlySet<Guid> manifestEvidence,
        IReadOnlySet<Guid> scaffoldEvidence)
    {
        if (job.Phase > MoveJobPhase.Planned
            || manifestEvidence.Contains(job.Id)
            || scaffoldEvidence.Contains(job.Id))
        {
            return JobEvidenceState.Owned;
        }

        try
        {
            if (!TryResolveRecoveryEvidenceEndpoint(
                    job,
                    target: false,
                    out var source)
                || !TryResolveRecoveryEvidenceEndpoint(
                    job,
                    target: true,
                    out var target))
            {
                return JobEvidenceState.Ambiguous;
            }
            if (target != null)
            {
                var targetMarker = Path.Join(
                    target,
                    $".listenarr-move-{job.Id:N}.pending");
                var targetParent = Path.GetDirectoryName(target);
                if (HasMarkerOrWriteFile(targetMarker)
                    || HasCleanupEvidence(targetParent, job.Id)
                    || (targetParent != null
                        && Directory.Exists(Path.Join(
                            targetParent,
                            Path.GetFileName(target) + ".tmp-" + job.Id.ToString("N"))))
                    || HasOwnedPartialFile(target, job.Id))
                {
                    return JobEvidenceState.Owned;
                }
            }

            if (source != null)
            {
                var sourceParent = Path.GetDirectoryName(source);
                var sourceMarker = Path.Join(
                    source,
                    $".listenarr-move-{job.Id:N}.pending");
                if (HasMarkerOrWriteFile(sourceMarker)
                    || HasCleanupEvidence(sourceParent, job.Id)
                    || (sourceParent != null
                        && Directory.Exists(Path.Join(
                            sourceParent,
                            $".listenarr-quarantine-{job.Id:N}"))))
                {
                    return JobEvidenceState.Owned;
                }
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return JobEvidenceState.Ambiguous;
        }

        return JobEvidenceState.None;
    }

    private static bool TryResolveRecoveryEvidenceEndpoint(
        MoveJob job,
        bool target,
        out string? resolvedPath)
    {
        var storedPath = target ? job.RequestedPath : job.SourcePath;
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            resolvedPath = null;
            return true;
        }

        var hasIdentity = target
            ? job.TryGetTargetIdentity(out var identity)
            : job.TryGetSourceIdentity(out identity);
        if (hasIdentity)
        {
            var resolved = FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                storedPath,
                identity,
                out var canonicalPath,
                out _);
            resolvedPath = resolved ? canonicalPath : null;
            return resolved;
        }

        var legacyResolved =
            FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                storedPath,
                out var legacyCanonicalPath,
                out _);
        resolvedPath = legacyResolved ? legacyCanonicalPath : null;
        return legacyResolved;
    }

    private static bool HasMarkerOrWriteFile(string markerPath)
    {
        if (File.Exists(markerPath))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(markerPath);
        return directory != null
            && Directory.Exists(directory)
            && Directory.EnumerateFiles(
                    directory,
                    Path.GetFileName(markerPath) + ".writing-*",
                    SearchOption.TopDirectoryOnly)
                .Any();
    }

    private static bool HasCleanupEvidence(string? parent, Guid jobId) =>
        parent != null
        && Directory.Exists(parent)
        && (Directory.EnumerateFiles(
                parent,
                $".listenarr-*-{jobId:N}.cleanup.json",
                SearchOption.TopDirectoryOnly)
            .Any()
            || Directory.EnumerateDirectories(
                    parent,
                    $".listenarr-*-{jobId:N}.cleanup-dir",
                    SearchOption.TopDirectoryOnly)
                .Any());

    private static bool HasOwnedPartialFile(string target, Guid jobId)
    {
        if (!Directory.Exists(target))
        {
            return false;
        }

        var suffix = $".listenarr-{jobId:N}.partial";
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((target, 0));
        var inspected = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth > MaximumEvidenceDepth)
            {
                throw new IOException("Move evidence exceeded the maximum directory depth.");
            }

            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Move evidence contains a linked directory.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                inspected++;
                if (inspected > MaximumEvidenceEntries)
                {
                    throw new IOException("Move evidence exceeded the maximum entry count.");
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Move evidence contains a linked entry.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, depth + 1));
                }
                else if (entry.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private enum JobEvidenceState
    {
        None,
        Owned,
        Ambiguous
    }

    private enum OwnershipEvidenceState
    {
        None,
        Valid,
        Ambiguous
    }

    private sealed record OwnershipEvidenceResult(
        OwnershipEvidenceState State,
        Guid? OwnerJobId,
        string? Error)
    {
        public static OwnershipEvidenceResult None { get; } = new(
            OwnershipEvidenceState.None,
            null,
            null);

        public static OwnershipEvidenceResult Valid(Guid ownerJobId) => new(
            OwnershipEvidenceState.Valid,
            ownerJobId,
            null);

        public static OwnershipEvidenceResult Ambiguous(string error) => new(
            OwnershipEvidenceState.Ambiguous,
            null,
            error);
    }

    private sealed record OwnershipMarkerIdentity(
        int Version,
        string? ArtifactType,
        Guid JobId,
        string? Source,
        string? Target,
        string? DirectoryPath,
        string? OwnedArtifactType = null,
        string? OwnedDirectoryPath = null);
}
